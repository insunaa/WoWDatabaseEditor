using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using DynamicData;
using Newtonsoft.Json;
using Prism.Commands;
using Prism.Events;
using PropertyChanged.SourceGenerator;
using WDE.Common;
using WDE.Common.Database;
using WDE.Common.History;
using WDE.Common.Managers;
using WDE.Common.Parameters;
using WDE.Common.Providers;
using WDE.Common.Services;
using WDE.Common.Services.MessageBox;
using WDE.Common.Sessions;
using WDE.Common.Solution;
using WDE.Common.Tasks;
using WDE.Common.Utils;
using WDE.DatabaseEditors.CustomCommands;
using WDE.DatabaseEditors.Data.Interfaces;
using WDE.DatabaseEditors.Data.Structs;
using WDE.DatabaseEditors.Extensions;
using WDE.DatabaseEditors.History;
using WDE.DatabaseEditors.Loaders;
using WDE.DatabaseEditors.Models;
using WDE.DatabaseEditors.QueryGenerators;
using WDE.DatabaseEditors.Services;
using WDE.DatabaseEditors.Solution;
using WDE.MVVM;
using WDE.MVVM.Observable;

namespace WDE.DatabaseEditors.ViewModels.MultiRow
{
    public partial class MultiRowDbTableEditorViewModel : ViewModelBase
    {
        private readonly IItemFromListProvider itemFromListProvider;
        private readonly IMessageBoxService messageBoxService;
        private readonly IParameterFactory parameterFactory;
        private readonly IDatabaseQueryExecutor mySqlExecutor;
        private readonly IQueryGenerator queryGenerator;
        private readonly IDatabaseTableModelGenerator modelGenerator;
        private readonly IConditionEditService conditionEditService;
        private readonly IDatabaseEditorsSettings editorSettings;
        private readonly ITablePersonalSettings tablePersonalSettings;
        private readonly IMetaColumnsSupportService metaColumnsSupportService;
        private readonly ICommentGeneratorService commentGeneratorService;
        private readonly DocumentMode mode;
        private readonly IDatabaseTableDataProvider tableDataProvider;

        private Dictionary<DatabaseKey, DatabaseEntitiesGroupViewModel> byEntryGroups = new();
        public ObservableCollection<DatabaseEntitiesGroupViewModel> Rows { get; } = new();

        public override ICommand Copy { get; }
        public override ICommand Paste { get; }
        public override ICommand Cut { get; }

        [Notify] private string searchText = "";
        
        [AlsoNotify(nameof(FocusedEntity))] 
        [AlsoNotify(nameof(SelectedRow))] 
        [AlsoNotify(nameof(FocusedCell))]
        [Notify] private VerticalCursor focusedRowIndex = VerticalCursor.None;
        
        [AlsoNotify(nameof(FocusedCell))]
        [Notify] private int focusedCellIndex = -1;
        public DatabaseCellViewModel? FocusedCell => SelectedRow != null && focusedCellIndex >= 0 && focusedCellIndex < SelectedRow.Cells.Count ? SelectedRow.Cells[focusedCellIndex] : null;
        public ITableMultiSelection MultiSelection { get; } = new TableMultiSelection();
        
        public DatabaseEntityViewModel? SelectedRow
        {
            get => focusedRowIndex.GroupIndex >= 0 && focusedRowIndex.GroupIndex < Rows.Count &&
                   focusedRowIndex.RowIndex >= 0 && focusedRowIndex.RowIndex < Rows[focusedRowIndex.GroupIndex].Count ?
                Rows[focusedRowIndex.GroupIndex][focusedRowIndex.RowIndex] : null;
        }

        public override bool SupportsMultiSelect => true;

        public override IReadOnlyList<DatabaseEntity>? MultiSelectionEntities
            => MultiSelection.All().Select(idx => Rows[idx.GroupIndex][idx.RowIndex].Entity).ToList();

        public override DatabaseEntity? FocusedEntity => SelectedRow?.Entity;
        
        public override DatabaseKey? SelectedTableKey => FocusedEntity?.ForceGenerateKey(tableDefinition);

        private MultiRowSplitMode splitMode;
        public bool SplitView => splitMode != MultiRowSplitMode.None;

        public bool SplitHorizontal
        {
            get => splitMode == MultiRowSplitMode.Horizontal;
            set
            {
                if (value && splitMode == MultiRowSplitMode.Horizontal)
                    return;
                
                splitMode = value ? MultiRowSplitMode.Horizontal : (splitMode == MultiRowSplitMode.Horizontal ? MultiRowSplitMode.None : splitMode);
                editorSettings.MultiRowSplitMode = splitMode;
                RaisePropertyChanged(nameof(SplitHorizontal));
                RaisePropertyChanged(nameof(SplitVertical));
                RaisePropertyChanged(nameof(SplitView));
            }
        }
        public bool SplitVertical
        {
            get => splitMode == MultiRowSplitMode.Vertical;
            set
            {
                if (value && splitMode == MultiRowSplitMode.Vertical)
                    return;
                
                splitMode = value ? MultiRowSplitMode.Vertical : (splitMode == MultiRowSplitMode.Vertical ? MultiRowSplitMode.None : splitMode);
                editorSettings.MultiRowSplitMode = splitMode;
                RaisePropertyChanged(nameof(SplitHorizontal));
                RaisePropertyChanged(nameof(SplitVertical));
                RaisePropertyChanged(nameof(SplitView));
            }
        }

        private IList<DatabaseColumnJson> columns = new List<DatabaseColumnJson>();
        public ObservableCollection<DatabaseColumnHeaderViewModel> Columns { get; } = new();
        private DatabaseColumnJson? autoIncrementColumn;

        private HashSet<DatabaseKey> keys = new();

        public bool AllowMultipleKeys
        {
            get => allowMultipleKeys;
            set => SetProperty(ref allowMultipleKeys, value);
        }
        public AsyncAutoCommand AddNewCommand { get; }
        public AsyncAutoCommand<DatabaseCellViewModel?> RemoveTemplateCommand { get; }
        public AsyncAutoCommand<DatabaseCellViewModel?> RevertCommand { get; }
        public AsyncAutoCommand<DatabaseCellViewModel?> EditConditionsCommand { get; }
        public DelegateCommand<DatabaseCellViewModel?> SetNullCommand { get; }
        public DelegateCommand<DatabaseCellViewModel?> DuplicateCommand { get; }
        public DelegateCommand<DatabaseEntitiesGroupViewModel> AddRowCommand { get; }
        public DelegateCommand<DatabaseEntitiesGroupViewModel> CollapseExpandCommand { get; }
        public AsyncAutoCommand<DatabaseCellViewModel> OpenParameterWindow { get; }
        public AsyncAutoCommand RevertSelectedCommand { get; }
        public DelegateCommand SetNullSelectedCommand { get; }
        public DelegateCommand DuplicateSelectedCommand { get; }
        public AsyncAutoCommand DeleteRowSelectedCommand { get; }
        public DelegateCommand InsertRowBelowCommand { get; }
        public DelegateCommand CopySelectedRowsCommand { get; }
        public DelegateCommand SelectAllCommand { get; }
        public AsyncAutoCommand PasteRowsCommand { get; } 

        public event Action<DatabaseEntity>? OnDeletedQuery;
        public event Action<DatabaseEntity>? OnKeyDeleted;
        public event Action<DatabaseKey>? OnKeyAdded;

        public MultiRowDbTableEditorViewModel(DatabaseTableSolutionItem solutionItem,
            IDatabaseTableDataProvider tableDataProvider, IItemFromListProvider itemFromListProvider,
            IHistoryManager history, ITaskRunner taskRunner, IMessageBoxService messageBoxService,
            IEventAggregator eventAggregator, ISolutionManager solutionManager, 
            IParameterFactory parameterFactory, ISolutionTasksService solutionTasksService,
            ISolutionItemNameRegistry solutionItemName, IDatabaseQueryExecutor mySqlExecutor,
            IQueryGenerator queryGenerator, IDatabaseTableModelGenerator modelGenerator,
            ITableDefinitionProvider tableDefinitionProvider,
            IConditionEditService conditionEditService, ISolutionItemIconRegistry iconRegistry,
            ISessionService sessionService, IDatabaseEditorsSettings editorSettings,
            IDatabaseTableCommandService commandService,
            IParameterPickerService parameterPickerService,
            IStatusBar statusBar, ITablePersonalSettings tablePersonalSettings,
            IMetaColumnsSupportService metaColumnsSupportService,
            IClipboardService clipboardService,
            ITextEntitySerializer serializer,
            ICommentGeneratorService commentGeneratorService,
            DocumentMode mode = DocumentMode.Editor) 
            : base(history, solutionItem, solutionItemName, 
            solutionManager, solutionTasksService, eventAggregator, 
            queryGenerator, tableDataProvider, messageBoxService, taskRunner, parameterFactory,
            tableDefinitionProvider, itemFromListProvider, iconRegistry, sessionService, commandService,
            parameterPickerService, statusBar, mySqlExecutor)
        {
            this.itemFromListProvider = itemFromListProvider;
            this.solutionItem = solutionItem;
            this.tableDataProvider = tableDataProvider;
            this.messageBoxService = messageBoxService;
            this.parameterFactory = parameterFactory;
            this.mySqlExecutor = mySqlExecutor;
            this.queryGenerator = queryGenerator;
            this.modelGenerator = modelGenerator;
            this.conditionEditService = conditionEditService;
            this.editorSettings = editorSettings;
            this.tablePersonalSettings = tablePersonalSettings;
            this.metaColumnsSupportService = metaColumnsSupportService;
            this.commentGeneratorService = commentGeneratorService;
            this.mode = mode;

            splitMode = editorSettings.MultiRowSplitMode;

            OpenParameterWindow = new AsyncAutoCommand<DatabaseCellViewModel>(EditParameter);
            RemoveTemplateCommand = new AsyncAutoCommand<DatabaseCellViewModel?>(RemoveTemplate, vm => vm != null);
            RevertCommand = new AsyncAutoCommand<DatabaseCellViewModel?>(Revert, cell => cell is DatabaseCellViewModel vm && vm.CanBeReverted && (vm.TableField?.IsModified ?? false));
            SetNullCommand = new DelegateCommand<DatabaseCellViewModel?>(SetToNull, vm => vm != null && vm.CanBeSetToNull);
            DuplicateCommand = new DelegateCommand<DatabaseCellViewModel?>(Duplicate, vm => vm != null);
            EditConditionsCommand = new AsyncAutoCommand<DatabaseCellViewModel?>(EditConditions);
            AddRowCommand = new DelegateCommand<DatabaseEntitiesGroupViewModel>(AddRowByGroup);
            CollapseExpandCommand = new DelegateCommand<DatabaseEntitiesGroupViewModel>(group => group.IsExpanded = !group.IsExpanded);
            AddNewCommand = new AsyncAutoCommand(AddNewEntity);
            InsertRowBelowCommand = new DelegateCommand(() =>
            {
                if (FocusedRowIndex.IsValid)
                {
                    var group = Rows[focusedRowIndex.GroupIndex];
                    AddRow(group.Key, focusedRowIndex.RowIndex + 1);
                }
                else if (Rows.Count > 0)
                {
                    var group = Rows[^1];
                    AddRow(group.Key, group.Count);
                }
            });
            RevertSelectedCommand = new AsyncAutoCommand(async () =>
            {
                if (focusedCellIndex == -1)
                    return;
                using var _ = BulkEdit("Batch revert");
                foreach (var selected in MultiSelection.All())
                {
                    var row = Rows[selected.GroupIndex][selected.RowIndex];
                    var cell = row.Cells[focusedCellIndex];
                    await RevertCommand.ExecuteAsync(cell);
                }
            });
            SetNullSelectedCommand = new DelegateCommand(() =>
            {
                if (focusedCellIndex == -1)
                    return;
                using var _ = BulkEdit("Batch set to null");
                foreach (var selected in MultiSelection.All())
                {
                    var row = Rows[selected.GroupIndex][selected.RowIndex];
                    var cell = row.Cells[focusedCellIndex];
                    SetNullCommand.Execute(cell);
                }
            });
            DuplicateSelectedCommand = new DelegateCommand(() =>
            {
                using var _ = BulkEdit("Batch duplicate");
                foreach (var selected in MultiSelection.All().Reverse())
                {
                    var row = Rows[selected.GroupIndex][selected.RowIndex];
                    var duplicate = row.Entity.Clone();
                    ForceInsertEntity(duplicate, selected.RowIndex + 1);
                }
            });
            DeleteRowSelectedCommand = new AsyncAutoCommand(async () =>
            {
                using var _ = BulkEdit("Batch delete");
                foreach (var selected in MultiSelection.All().Reverse())
                {
                    var row = Rows[selected.GroupIndex][selected.RowIndex];
                    await RemoveEntity(row.Entity);
                }
                MultiSelection.Clear();
            });
            Copy = new DelegateCommand(() =>
            {
                clipboardService.SetText(FocusedCell!.StringValue ?? "(null)");
            }, () => FocusedCell != null).ObservesProperty(()=>FocusedCell);

            Cut = new DelegateCommand(() =>
            {
                Copy.Execute(null);
                if (FocusedCell!.CanBeSetToNull)
                    FocusedCell!.ParameterValue?.SetNull();
                else if (FocusedCell!.ParameterValue is IParameterValue<long> longValue)
                    longValue.Value = 0;
                else
                    FocusedCell!.UpdateFromString("");
            }, () => FocusedCell != null).ObservesProperty(()=>FocusedCell);
            Paste = new AsyncAutoCommand(async () =>
            {
                if (await clipboardService.GetText() is { } text)
                {
                    using (BulkEdit("Bulk paste"))
                    {
                        foreach (var index in MultiSelection.All())
                            Rows[index.GroupIndex][index.RowIndex].Cells[focusedCellIndex].UpdateFromString(text);
                    }
                }
            }, () => FocusedCell != null);
            CopySelectedRowsCommand = new DelegateCommand(() =>
            {
                var serialized = serializer.Serialize(MultiSelection.All()
                    .Select(index => Rows[index.GroupIndex][index.RowIndex].Entity));
                clipboardService.SetText(serialized);
            });
            PasteRowsCommand = new AsyncAutoCommand(async () =>
            {
                IDisposable? bulk = null;
                try
                {
                    var text = await clipboardService.GetText();
                    var key = FocusedEntity?.Key ?? (Rows.Count > 0 ? Rows[0].Key : null);
                    var deserialized = serializer.Deserialize(tableDefinition, text, key);
                    if (deserialized.Count > 1)
                        bulk = BulkEdit("Paste rows");
                    int index = focusedRowIndex.RowIndex == -1 ? (Rows.Count > 0 ? Rows[0].Count : 0) : focusedRowIndex.RowIndex + 1;
                    foreach (var entity in deserialized)
                    {
                        if (key.HasValue)
                            entity.SetTypedCellOrThrow(tableDefinition.PrimaryKey[0], key.Value[0]);
                        ForceInsertEntity(entity, index++);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    statusBar.PublishNotification(new PlainNotification(NotificationType.Error, "Failed to paste rows (see the debug log output)"));
                }
                finally
                {
                    bulk?.Dispose();
                }
            });
            var cutRowsCommand = new DelegateCommand(() =>
            {
                CopySelectedRowsCommand.Execute();
                DeleteRowSelectedCommand.Execute(null);
            });
            SelectAllCommand = new DelegateCommand(() =>
            {
                if (Rows.Count == 0)
                    return;
                
                var groupIndex = focusedRowIndex.GroupIndex < 0 || focusedRowIndex.GroupIndex >= Rows.Count ? 0 : focusedRowIndex.GroupIndex;
                var rowsInGroup = Rows[groupIndex].Count;
                
                MultiSelection.Clear();
                for (var row = 0; row < rowsInGroup; row++)
                    MultiSelection.Add(new VerticalCursor(groupIndex, row));
            });
            KeyBindings.Add(new CommandKeyBinding(CopySelectedRowsCommand, "Ctrl+C"));
            KeyBindings.Add(new CommandKeyBinding(CopySelectedRowsCommand, "Cmd+C"));
            KeyBindings.Add(new CommandKeyBinding(PasteRowsCommand, "Ctrl+V"));
            KeyBindings.Add(new CommandKeyBinding(PasteRowsCommand, "Cmd+V"));
            KeyBindings.Add(new CommandKeyBinding(SelectAllCommand, "Ctrl+A"));
            KeyBindings.Add(new CommandKeyBinding(SelectAllCommand, "Cmd+A"));
            KeyBindings.Add(new CommandKeyBinding(cutRowsCommand, "Ctrl+X"));
            KeyBindings.Add(new CommandKeyBinding(cutRowsCommand, "Cmd+X"));
            
            Debug.Assert(tableDefinition.GroupByKeys.Count == 1);

            ScheduleLoading();
        }

        public override DatabaseEntity AddRow(DatabaseKey key, int? index)
        {
            var freshEntity = modelGenerator.CreateEmptyEntity(tableDefinition, key, false);
            if (autoIncrementColumn != null)
            {
                long max = 0;
                
                if (byEntryGroups[key].Count > 0)
                    max = 1 + byEntryGroups[key].Max(t =>
                    {
                        if (t.Entity.GetCell(autoIncrementColumn.DbColumnName) is DatabaseField<long> lField)
                            return lField.Current.Value;
                        return 0L;
                    });
                
                if (freshEntity.GetCell(autoIncrementColumn.DbColumnName) is DatabaseField<long> lField)
                    lField.Current.Value = max;
            }

            int elementsInGroup = byEntryGroups.TryGetValue(key, out var group) ? group.Count : 0;
            ForceInsertEntity(freshEntity, index ?? elementsInGroup);
            return freshEntity;
        }
        
        private void AddRowByGroup(DatabaseEntitiesGroupViewModel group)
        {
            AddRow(group.Key, null);
        }

        private async Task AddNewEntity()
        {
            var parameter = parameterFactory.Factory(tableDefinition.Picker);
            var keys = await parameterPickerService.PickMultiple(parameter);

            if (keys.Count > 150)
            {
                await messageBoxService.SimpleDialog("Too many items", "Too many items", "You are trying to add too many items at once, sorry");
                return;
            }
            
            Debug.Assert(tableDefinition.GroupByKeys.Count == 1);

            var containedKeys = keys.Where(val => ContainsKey(new DatabaseKey(val))).ToList();
            
            if (containedKeys.Count > 0)
            {
                await messageBoxService.ShowDialog(new MessageBoxFactory<bool>()
                    .SetTitle("Key already added")
                    .SetMainInstruction($"Key {containedKeys[0]} is already added to this editor")
                    .SetContent("To add a new row, click (+) sign next to the key name")
                    .WithOkButton(true)
                    .SetIcon(MessageBoxIcon.Warning)
                    .Build());
            }
            
            using var _ = BulkEdit("Load data");
            foreach (var keyValue in keys)
            {
                DatabaseKey key = new DatabaseKey(keyValue);

                if (ContainsKey(key))
                    continue;
            
                var data = await tableDataProvider.Load(tableDefinition.Id, null, null,null, new[]{key});
                if (data == null) 
                    continue;

                OnKeyAdded?.Invoke(key);
                EnsureKey(key);
                AddEntities(data.Entities);   
            }
        }
        
        private void SetToNull(DatabaseCellViewModel? view)
        {
            if (view != null && view.CanBeNull && !view.IsReadOnly) 
                view.ParameterValue?.SetNull();
        }

        private void Duplicate(DatabaseCellViewModel? view)
        {
            if (view != null)
            {
                var duplicate = view.Parent.Entity.Clone();
                var index = byEntryGroups[view.Parent.Key].IndexOf(view.Parent);
                ForceInsertEntity(duplicate, index + 1);
            }
        }

        private async Task EditConditions(DatabaseCellViewModel? view)
        {
            if (view == null)
                return;
            
            var conditionList = view.ParentEntity.Conditions;
            
            var newConditions = await conditionEditService.EditConditions(tableDefinition.Condition!.SourceType, conditionList);
            if (newConditions == null)
                return;

            view.ParentEntity.Conditions = newConditions.ToList();
            if (tableDefinition.Condition.SetColumn != null)
            {
                var hasColumn = view.ParentEntity.GetCell(tableDefinition.Condition.SetColumn);
                if (hasColumn is DatabaseField<long> lf)
                    lf.Current.Value = view.ParentEntity.Conditions.Count > 0 ? 1 : 0;
            }
        }
        
        private async Task Revert(DatabaseCellViewModel? view)
        {
            if (view == null || view.IsReadOnly)
                return;
            
            view.ParameterValue?.Revert();
        }

        private async Task RemoveTemplate(DatabaseCellViewModel? view)
        {
            if (view == null)
                return;

            await RemoveEntity(view.ParentEntity);
        }

        private Task EditParameter(DatabaseCellViewModel cell)
        {
            if (cell.ParameterValue != null)
                return EditParameter(cell.ParameterValue, cell.ParentEntity);
            return Task.CompletedTask;
        }

        protected override IReadOnlyList<DatabaseKey> GenerateKeys() => keys.ToList();
        protected override IReadOnlyList<DatabaseKey>? GenerateDeletedKeys() => null;

        protected override async Task InternalLoadData(DatabaseTableData data)
        {
            Rows.RemoveAll();
            columns = tableDefinition.Groups.SelectMany(g => g.Fields)
                .Where(c => c.DbColumnName != data.TableDefinition.TablePrimaryKeyColumnName)
                .ToList();
            if (mode == DocumentMode.PickRow)
                columns.Insert(0, new DatabaseColumnJson(){Name="Pick", Meta = "picker", PreferredWidth = 30});
            autoIncrementColumn = columns.FirstOrDefault(c => c.AutoIncrement);
            if (Columns.Count == 0)
            {
                Columns.AddRange(columns.Select(c =>
                {
                    var column = new DatabaseColumnHeaderViewModel(c);
                    column.Width = tablePersonalSettings.GetColumnWidth(TableDefinition.Id, column.ColumnIdForUi, c.PreferredWidth ?? 120);
                    return AutoDispose(column);
                }));
                Columns.Each(col => col.ToObservable(c => c.Width)
                    .Skip(1)
                    .Throttle(TimeSpan.FromMilliseconds(300))
                    .Subscribe(width =>
                    {
                        tablePersonalSettings.UpdateWidth(TableDefinition.Id, col.ColumnIdForUi, col.PreferredWidth ?? 100,  ((int)(width) / 5) * 5);
                    }));   
            }
            
            foreach (var entity in solutionItem.Entries)
                EnsureKey(entity.Key);

            AddEntities(data.Entities);
            historyHandler = History.AddHandler(AutoDispose(new MultiRowTableEditorHistoryHandler(this)));
        }

        public override IDisposable BulkEdit(string name) => historyHandler?.BulkEdit(name) ?? Disposable.Empty;

        private MultiRowTableEditorHistoryHandler? historyHandler;
        private bool allowMultipleKeys = true;

        protected override void UpdateSolutionItem()
        {
            solutionItem.Entries = keys.Select(e =>
                new SolutionItemDatabaseEntity(e, false, false)).ToList();
        }

        public async Task<bool> RemoveEntity(DatabaseEntity entity)
        {
            var itemsWithSameKey = Entities.Count(e => e.Key == entity.Key);

            var removed = ForceRemoveEntity(entity);
            
            if (itemsWithSameKey == 1)
            {
                if (await messageBoxService.ShowDialog(new MessageBoxFactory<bool>()
                    .SetTitle("Removing entity")
                    .SetMainInstruction($"Do you want to delete the key {entity.Key} from solution?")
                    .SetContent(
                        $"This entity is the last row with key {entity.Key}. You have to choose if you want to delete the key from the solution as well.\n\nIf you delete it from the solution, DELETE FROM... will no longer be generated for this key.")
                    .WithYesButton(true)
                    .WithNoButton(false)
                    .SetIcon(MessageBoxIcon.Information)
                    .Build()))
                {
                    if (mySqlExecutor.IsConnected(tableDefinition))
                    {
                        if (await messageBoxService.ShowDialog(new MessageBoxFactory<bool>()
                            .SetTitle("Execute DELETE query?")
                            .SetMainInstruction("Do you want to execute DELETE query now?")
                            .SetContent(
                                "You have decided to remove the item from solution, therefore DELETE FROM query will not be generated for this key anymore, you we can execute DELETE with that key for that last time.")
                            .WithYesButton(true)
                            .WithNoButton(false)
                            .Build()))
                        {
                            OnDeletedQuery?.Invoke(entity);
                            await mySqlExecutor.ExecuteSql(tableDefinition, queryGenerator.GenerateDeleteQuery(tableDefinition, entity));
                            History.MarkNoSave();
                        }
                    }
                    RemoveKey(entity.Key);
                    OnKeyDeleted?.Invoke(entity);
                }
            }
            
            return removed;
        }
        
        public void RedoExecuteDelete(DatabaseEntity entity)
        {
            if (mySqlExecutor.IsConnected(tableDefinition))
            {
                mySqlExecutor.ExecuteSql(tableDefinition, queryGenerator.GenerateDeleteQuery(tableDefinition, entity));
                History.MarkNoSave();
            }
        }
        
        public void DoAddKey(DatabaseKey entity)
        {
            EnsureKey(entity);
        }

        public void UndoAddKey(DatabaseKey entity)
        {
            RemoveKey(entity);
        }
        
        public override bool ForceRemoveEntity(DatabaseEntity entity)
        {
            var indexOfEntity = Entities.GetIndex(entity);
            if (indexOfEntity.index == -1)
                return false;

            var vm = byEntryGroups[entity.Key][indexOfEntity.index];
                
            DoAction(new AnonymousHistoryAction("Remove row", () =>
            {
                entities[indexOfEntity.group].Insert(indexOfEntity.index, entity);
                byEntryGroups[entity.Key].Insert(indexOfEntity.index, vm);
            }, () =>
            {
                entities[indexOfEntity.group].RemoveAt(indexOfEntity.index);
                var vm = byEntryGroups[entity.Key].GetAndRemove(entity);
                if (SelectedRow == vm)
                    FocusedRowIndex = VerticalCursor.None;    
            }));
            
            return true;
        }

        private void DoAction(IHistoryAction action)
        {
            if (historyHandler != null)
                historyHandler.DoAction(action);
            else
                action.Redo();
        }

        public bool AddEntity(DatabaseEntity entity, int index)
        {
            return ForceInsertEntity(entity, index);
        }

        public override bool ForceInsertEntity(DatabaseEntity entity, int index, bool undoing = false)
        {
            var name = parameterFactory.Factory(tableDefinition.Picker).ToString(entity.Key[0]);
            var row = new DatabaseEntityViewModel(entity, name);
            
            int columnIndex = 0;
            foreach (var column in columns)
            {
                DatabaseCellViewModel cellViewModel;

                if (column.IsConditionColumn)
                {
                    var label = Observable.Select(entity.ToObservable(e => e.Conditions), c => "Conditions (" + c.CountActualConditions() + ")");
                    cellViewModel = AutoDispose(new DatabaseCellViewModel(columnIndex, "Conditions", EditConditionsCommand, row, entity, label));
                }
                else if (column.IsMetaColumn)
                {
                    var (command, title) = metaColumnsSupportService.GenerateCommand(this, column.Meta!, entity, entity.GenerateKey(TableDefinition));
                    cellViewModel = AutoDispose(new DatabaseCellViewModel(columnIndex, column.Name, command, row, entity, title));
                }
                else
                {
                    var cell = entity.GetCell(column.DbColumnName);
                    if (cell == null)
                        throw new Exception("this should never happen");

                    IParameterValue parameterValue = null!;
                    if (cell is DatabaseField<long> longParam)
                    {
                        parameterValue = new ParameterValue<long, DatabaseEntity>(entity, longParam.Current, longParam.Original, parameterFactory.Factory(column.ValueType));
                    }
                    else if (cell is DatabaseField<string> stringParam)
                    {
                        if (column.AutogenerateComment != null)
                        {
                            var autoGenComment = commentGeneratorService.GenerateAutoCommentOnly(entity, TableDefinition, column.DbColumnName);
                            
                            stringParam.Current.Value = stringParam.Current.Value.GetCommentUnlessDefault(autoGenComment, column.CanBeNull);
                            stringParam.Original.Value = stringParam.Original.Value.GetCommentUnlessDefault(autoGenComment, column.CanBeNull);
                        }
                        parameterValue = new ParameterValue<string, DatabaseEntity>(entity, stringParam.Current, stringParam.Original, parameterFactory.FactoryString(column.ValueType));
                    }
                    else if (cell is DatabaseField<float> floatParameter)
                    {
                        parameterValue = new ParameterValue<float, DatabaseEntity>(entity, floatParameter.Current, floatParameter.Original, FloatParameter.Instance);
                    }

                    parameterValue.DefaultIsBlank = column.IsZeroBlank;

                    cellViewModel = AutoDispose(new DatabaseCellViewModel(columnIndex, column, row, entity, cell, parameterValue));
                }
                row.Cells.Add(cellViewModel);
                columnIndex++;
            }

            var action = new AnonymousHistoryAction("New row", () =>
            {
                if (!byEntryGroups.TryGetValue(entity.Key, out var group))
                    return;
                
                var groupIndex = Rows.IndexOf(group);
                
                group.RemoveAt(index);
                entities[groupIndex].RemoveAt(index);
            }, () =>
            {
                EnsureKey(entity.Key);
                var group = byEntryGroups[entity.Key];
                var groupIndex = Rows.IndexOf(group);
                entities[groupIndex].Insert(index, entity);
                group.Insert(index, row);
                FocusedRowIndex = new VerticalCursor(groupIndex, index);
                if (focusedRowIndex.IsValid)
                {
                    MultiSelection.Clear();
                    MultiSelection.Add(focusedRowIndex);
                }
            });
            DoAction(action);

            return true;
        }

        private void RemoveKey(DatabaseKey entity)
        {
            if (keys.Remove(entity))
            {
                var index = Rows.IndexOf(byEntryGroups[entity]);
                Rows.RemoveAt(index);
                entities.RemoveAt(index);
                byEntryGroups.Remove(entity);
            }
        }

        private bool ContainsKey(DatabaseKey key)
        {
            return keys.Contains(key);
        }
        
        private void EnsureKey(DatabaseKey entity)
        {
            if (keys.Add(entity))
            {
                byEntryGroups[entity] = new DatabaseEntitiesGroupViewModel(entity, GenerateName(entity[0]));
                Rows.Add(byEntryGroups[entity]);
                entities.Add(new CustomObservableCollection<DatabaseEntity>());
            }
        }

        private void AddEntities(IReadOnlyList<DatabaseEntity> tableDataEntities)
        {
            int index = 0;
            if (tableDataEntities.Count > 0 && byEntryGroups.TryGetValue(tableDataEntities[0].Key, out var group))
                index = group.Count;
            
            foreach (var entity in tableDataEntities)
                AddEntity(entity, index++);
        }
        
        protected override List<EntityOrigianlField>? GetOriginalFields(DatabaseEntity entity)
        {
            return null; // because in multirow we always delete and reinsert all
        }

        public void UpdateSelectedCells(string text)
        {
            System.IDisposable disposable = new EmptyDisposable();
            if (MultiSelection.MoreThanOne)
                disposable = BulkEdit("Bulk edit property");
            foreach (var selected in MultiSelection.All())
                Rows[selected.GroupIndex].Rows[selected.RowIndex].CellsList[focusedCellIndex].UpdateFromString(text);
            disposable.Dispose();
        }

        public void SortByColumn(DatabaseColumnHeaderViewModel column, bool ascending)
        {
            List<List<int>> newGroupIndices = new List<List<int>>();
            List<List<int>> oldGroupIndices = new List<List<int>>();

            var primaryColumnIndex = Columns.IndexIf(i => i == column);
            int secondarColumnIndex = -1;
            if (TableDefinition.PrimaryKey.Count >= 3)
            {
                // i.e. for creature_text, primary keys are (entry, group, id)
                // in MultiRow editor `entry` is hidden, because entries are grouped by it, so we want to check if
                // the second primary key column is the one we sort by, because in this case, we want to sort by the third column as well
                if (TableDefinition.PrimaryKey[0] == TableDefinition.TablePrimaryKeyColumnName &&
                    TableDefinition.PrimaryKey[1] == column.DatabaseName)
                    secondarColumnIndex = Columns.IndexIf(c => c.DatabaseName == TableDefinition.PrimaryKey[2]);
            }

            foreach (var group in Rows)
            {
                var newIndices = group.NewIndicesIfSorted(ascending, Compare.CreateComparer<DatabaseEntityViewModel>((a, b) =>
                {
                    var cellA = a!.Cells[primaryColumnIndex];
                    var cellB = b!.Cells[primaryColumnIndex];
                    int compareResult;
                    if (cellA.IsLongValue)
                        compareResult = cellA.AsLongValue.CompareTo(cellB.AsLongValue);
                    else
                        compareResult = string.Compare(cellA.StringValue, cellB.StringValue, StringComparison.Ordinal);
                    
                    if (compareResult != 0)
                        return compareResult;
                    
                    // secondary column index, if exists, will be always long
                    if (secondarColumnIndex != -1)
                    {
                        var cellA_2 = a.Cells[secondarColumnIndex];
                        var cellB_2 = b.Cells[secondarColumnIndex];
                        return cellA_2.AsLongValue.CompareTo(cellB_2.AsLongValue);
                    }

                    return 0;
                }), out var oldIndices).ToList();
                newGroupIndices.Add(newIndices);
                oldGroupIndices.Add(oldIndices);
            }
            var action = new AnonymousHistoryAction("Sort by " + column.Name, () =>
            {
                for (var index = 0; index < newGroupIndices.Count; index++)
                {
                    var group = Rows[index];
                    EntitiesObservable[index].OrderByIndices(newGroupIndices[index]);
                    group.OrderByIndices(newGroupIndices[index]);
                }
            }, () =>
            {
                for (var index = 0; index < oldGroupIndices.Count; index++)
                {
                    var group = Rows[index];
                    EntitiesObservable[index].OrderByIndices(oldGroupIndices[index]);
                    group.OrderByIndices(oldGroupIndices[index]);
                }
            });
            DoAction(action);
        }

        // we're gonna check for duplicate keys before saving the data to prevent data loss
        protected override async Task<bool> BeforeSaveData()
        {
            HashSet<DatabaseKey> keys = new HashSet<DatabaseKey>();
            bool anyDuplicate = false;
            foreach (var group in Rows)
            {
                keys.Clear();
                foreach (var row in group)
                {
                    var uniqueKey = row.Entity.ForceGenerateKey(tableDefinition);
                    if (!keys.Add(uniqueKey))
                    {
                        row.Duplicate = true;
                        anyDuplicate = true;
                    }
                    else
                        row.Duplicate = false;
                }
            }

            if (anyDuplicate)
            {
                await messageBoxService.SimpleDialog("Duplicates", "There are duplicates in the table",
                    "The table can't be saved, because there are duplicate rows (same primary key). They are marked in red.\n\nEither change their key values or delete them.");
                return false;
            }

            return true;
        }
    }
}
