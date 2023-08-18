#if UNITY_2022_1_OR_NEWER
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Unity.MemoryProfiler.Editor.UI
{
    class AllTrackedMemoryBreakdownViewController : ViewController
    {
        public const string BreakdownName = "All Tracked Memory";
        const string k_UxmlAssetGuid = "e4f3ed07cb2c2ad4a9d9afed10a6e549";
        const string k_UssClass_Dark = "all-tracked-memory-breakdown-view__dark";
        const string k_UssClass_Light = "all-tracked-memory-breakdown-view__light";
        const string k_UxmlIdentifier_SearchField = "all-tracked-memory-breakdown-view__search-field";
        const string k_UxmlIdentifier_TotalBar = "all-tracked-memory-breakdown-view__total__bar";
        const string k_UxmlIdentifier_TotalInTableLabel = "all-tracked-memory-breakdown-view__total-footer__table-label";
        const string k_UxmlIdentifier_TotalInSnapshotLabel = "all-tracked-memory-breakdown-view__total-footer__snapshot-label";
        const string k_UxmlIdentifier_TreeView = "all-tracked-memory-breakdown-view__tree-view";
        const string k_UxmlIdentifier_TreeViewColumn__Description = "all-tracked-memory-breakdown-view__tree-view__column__description";
        const string k_UxmlIdentifier_TreeViewColumn__Size = "all-tracked-memory-breakdown-view__tree-view__column__size";
        const string k_UxmlIdentifier_TreeViewColumn__SizeBar = "all-tracked-memory-breakdown-view__tree-view__column__size-bar";
        const string k_UxmlIdentifier_LoadingOverlay = "all-tracked-memory-breakdown-view__loading-overlay";
        const string k_UxmlIdentifier_ErrorLabel = "all-tracked-memory-breakdown-view__error-label";
        const string k_ErrorMessage = "Snapshot is from an outdated Unity version that is not fully supported.";

        // Model.
        readonly CachedSnapshot m_Snapshot;
        AllTrackedMemoryBreakdownModel m_Model;
        AsyncWorker<AllTrackedMemoryBreakdownModel> m_BuildModelWorker;

        // View.
        ToolbarSearchField m_SearchField;
        ProgressBar m_TotalBar;
        Label m_TotalInTableLabel;
        Label m_TotalInSnapshotLabel;
        MultiColumnTreeView m_TreeView;
        ActivityIndicatorOverlay m_LoadingOverlay;
        Label m_ErrorLabel;


        public AllTrackedMemoryBreakdownViewController(CachedSnapshot snapshot)
        {
            m_Snapshot = snapshot;
        }

        protected override VisualElement LoadView()
        {
            var view = ViewControllerUtility.LoadVisualTreeFromUxml(k_UxmlAssetGuid);
            if (view == null)
                throw new InvalidOperationException("Unable to create view from Uxml. Uxml must contain at least one child element.");
            view.style.flexGrow = 1;

            var themeUssClass = (EditorGUIUtility.isProSkin) ? k_UssClass_Dark : k_UssClass_Light;
            view.AddToClassList(themeUssClass);

            return view;
        }

        protected override void ViewLoaded()
        {
            GatherViewReferences();
            ConfigureTreeView();

            m_SearchField.RegisterValueChangedCallback(OnSearchValueChanged);
            m_SearchField.RegisterCallback<FocusOutEvent>(OnSearchFocusLost);

            // These styles are not supported in Unity 2020 and earlier. They will cause project errors if included in the stylesheet in those Editor versions.
            // Remove when we drop support for <= 2020 and uncomment these styles in the stylesheet.
            var transitionDuration = new StyleList<TimeValue>(new List<TimeValue>() { new TimeValue(0.23f) });
            var transitionTimingFunction = new StyleList<EasingFunction>(new List<EasingFunction>() { new EasingFunction(EasingMode.EaseOut) });
            m_TotalBar.Fill.style.transitionDuration = transitionDuration;
            m_TotalBar.Fill.style.transitionProperty = new StyleList<StylePropertyName>(new List<StylePropertyName>() { new StylePropertyName("width") });
            m_TotalBar.Fill.style.transitionTimingFunction = transitionTimingFunction;
            m_LoadingOverlay.style.transitionDuration = transitionDuration;
            m_LoadingOverlay.style.transitionProperty = new StyleList<StylePropertyName>(new List<StylePropertyName>() { new StylePropertyName("opacity") });
            m_LoadingOverlay.style.transitionTimingFunction = transitionTimingFunction;

            BuildModelAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                m_BuildModelWorker?.Dispose();

            base.Dispose(disposing);
        }

        void GatherViewReferences()
        {
            m_SearchField = View.Q<ToolbarSearchField>(k_UxmlIdentifier_SearchField);
            m_TotalBar = View.Q<ProgressBar>(k_UxmlIdentifier_TotalBar);
            m_TotalInTableLabel = View.Q<Label>(k_UxmlIdentifier_TotalInTableLabel);
            m_TotalInSnapshotLabel = View.Q<Label>(k_UxmlIdentifier_TotalInSnapshotLabel);
            m_TreeView = View.Q<MultiColumnTreeView>(k_UxmlIdentifier_TreeView);
            m_LoadingOverlay = View.Q<ActivityIndicatorOverlay>(k_UxmlIdentifier_LoadingOverlay);
            m_ErrorLabel = View.Q<Label>(k_UxmlIdentifier_ErrorLabel);
        }

        void ConfigureTreeView()
        {
            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__Description, "Description", BindCellForDescriptionColumn(), AllTrackedMemoryDescriptionCell.Instantiate);
            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__Size, "Total Size", BindCellForSizeColumn());
            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__SizeBar, "Total Size Bar", BindCellForSizeBarColumn(), MakeSizeBarCell);

            m_TreeView.onSelectionChange += OnTreeViewSelectionChanged;
            m_TreeView.columnSortingChanged += OnTreeViewSortingChanged;
        }

        void ConfigureTreeViewColumn(string columnName, string columnTitle, Action<VisualElement, int> bindCell, Func<VisualElement> makeCell = null)
        {
            var column = m_TreeView.columns[columnName];
            column.title = columnTitle;
            column.bindCell = bindCell;
            if (makeCell != null)
                column.makeCell = makeCell;
        }

        void BuildModelAsync()
        {
            // Cancel existing build if necessary.
            m_BuildModelWorker?.Dispose();

            // Show loading UI.
            m_LoadingOverlay.Show();

            // Dispatch asynchronous build.
            var snapshot = m_Snapshot;
            var nameFilter = m_SearchField.value;
            var args = new AllTrackedMemoryBreakdownModelBuilder.BuildArgs(nameFilter);
            var sortDescriptors = BuildSortDescriptorsFromTreeView();
            m_BuildModelWorker = new AsyncWorker<AllTrackedMemoryBreakdownModel>();
            m_BuildModelWorker.Execute(() =>
            {
                try
                {
                    // Build the data model.
                    var modelBuilder = new AllTrackedMemoryBreakdownModelBuilder();
                    var model = modelBuilder.Build(snapshot, args);

                    // Sort it according to the current sort descriptors.
                    model.Sort(sortDescriptors);

                    return model;
                }
                catch (Exception)
                {
                    return null;
                }
            }, (model) =>
                {
                    // Update model.
                    m_Model = model;

                    if (model != null)
                    {
                        // Refresh UI with new data model.
                        RefreshView();
                    }
                    else
                    {
                        // Display error message.
                        m_ErrorLabel.text = k_ErrorMessage;
                        UIElementsHelper.SetElementDisplay(m_ErrorLabel, true);
                    }

                    // Hide loading UI.
                    m_LoadingOverlay.Hide();
                });
        }

        void RefreshView()
        {
            var progress = (float)m_Model.TotalMemorySize / m_Model.TotalSnapshotMemorySize;
            m_TotalBar.SetProgress(progress);

            var totalMemorySizeText = EditorUtility.FormatBytes((long)m_Model.TotalMemorySize);
            m_TotalInTableLabel.text = $"Total Memory In Table: {totalMemorySizeText}";

            var totalSnapshotMemorySizeText = EditorUtility.FormatBytes((long)m_Model.TotalSnapshotMemorySize);
            m_TotalInSnapshotLabel.text = $"Total Memory In Snapshot: {totalSnapshotMemorySizeText}";

            m_TreeView.SetRootItems(m_Model.RootNodes);
            m_TreeView.Rebuild();
        }

        Action<VisualElement, int> BindCellForDescriptionColumn()
        {
            const string k_NoName = "<No Name>";
            return (element, rowIndex) =>
            {
                var cell = (AllTrackedMemoryDescriptionCell)element;
                var itemData = m_TreeView.GetItemDataForIndex<AllTrackedMemoryBreakdownModel.ItemData>(rowIndex);

                var displayText = itemData.Name;
                if (string.IsNullOrEmpty(displayText))
                    displayText = k_NoName;
                cell.SetText(displayText);

                var secondaryDisplayText = string.Empty;
                var childCount = itemData.ChildCount;
                if (childCount > 0)
                    secondaryDisplayText = $"({childCount} Item{((childCount > 1) ? "s" : string.Empty)})";
                cell.SetSecondaryText(secondaryDisplayText);
            };
        }

        Action<VisualElement, int> BindCellForSizeBarColumn()
        {
            return (element, rowIndex) =>
            {
                var size = m_TreeView.GetItemDataForIndex<AllTrackedMemoryBreakdownModel.ItemData>(rowIndex).Size;
                var progress = (float)size / m_Model.TotalMemorySize;

                var cell = (ProgressBar)element;
                cell.SetProgress(progress);
            };
        }

        Action<VisualElement, int> BindCellForSizeColumn()
        {
            return (element, rowIndex) =>
            {
                var itemData = m_TreeView.GetItemDataForIndex<AllTrackedMemoryBreakdownModel.ItemData>(rowIndex);
                var size = itemData.Size;
                ((Label)element).text = EditorUtility.FormatBytes((long)size);
            };
        }

        VisualElement MakeSizeBarCell()
        {
            var cell = new ProgressBar()
            {
                style =
                {
                    flexGrow = 1,
                    marginBottom = 8,
                    marginLeft = 8,
                    marginRight = 8,
                    marginTop = 8,
                }
            };

            cell.Fill.style.backgroundColor = UnityEngine.Color.white;
            cell.Fill.style.minWidth = 1;

            return cell;
        }

        void OnSearchValueChanged(ChangeEvent<string> evt)
        {
            BuildModelAsync();
        }

        void OnSearchFocusLost(FocusOutEvent evt)
        {
            MemoryProfilerAnalytics.AddInteractionCountToEvent<MemoryProfilerAnalytics.InteractionsInPage, MemoryProfilerAnalytics.PageInteractionType>(
                MemoryProfilerAnalytics.PageInteractionType.SearchInPageWasUsed);
        }

        void OnTreeViewSortingChanged()
        {
            BuildModelAsync();

            var sortedColumns = m_TreeView.sortedColumns.GetEnumerator();
            if (sortedColumns.MoveNext())
            {
                MemoryProfilerAnalytics.StartEvent<MemoryProfilerAnalytics.SortedColumnEvent>();
                MemoryProfilerAnalytics.EndEvent(new MemoryProfilerAnalytics.SortedColumnEvent() { viewName = BreakdownName, Ascending = sortedColumns.Current.direction == SortDirection.Ascending, shown = sortedColumns.Current.columnIndex, fileName = sortedColumns.Current.columnName });

                MemoryProfilerAnalytics.AddInteractionCountToEvent<MemoryProfilerAnalytics.InteractionsInPage, MemoryProfilerAnalytics.PageInteractionType>(
                    MemoryProfilerAnalytics.PageInteractionType.TableSortingWasChanged);
            }
        }

        void OnTreeViewSelectionChanged(IEnumerable<object> items)
        {
            var selectedIndex = m_TreeView.selectedIndex;
            var data = m_TreeView.GetItemDataForIndex<AllTrackedMemoryBreakdownModel.ItemData>(selectedIndex);
            var dataIndex = data.DataIndex;

            // Temporary integration with legacy history/selection API to show UI for some selected items in the Details view.
            var selection = MemorySampleSelection.InvalidMainSelection;
            switch (dataIndex.DataType)
            {
                case AllTrackedMemoryBreakdownModel.CachedSnapshotDataIndex.Type.NativeObject:
                {
                    var nativeObjectIndex = dataIndex.Index;
                    selection = MemorySampleSelection.FromNativeObjectIndex(nativeObjectIndex);
                    break;
                }

                case AllTrackedMemoryBreakdownModel.CachedSnapshotDataIndex.Type.NativeType:
                {
                    var nativeTypeIndex = dataIndex.Index;
                    selection = MemorySampleSelection.FromNativeTypeIndex(nativeTypeIndex);
                    break;
                }

                case AllTrackedMemoryBreakdownModel.CachedSnapshotDataIndex.Type.ManagedObject:
                {
                    var managedObjectIndex = dataIndex.Index;
                    selection = MemorySampleSelection.FromManagedObjectIndex(managedObjectIndex);
                    break;
                }

                case AllTrackedMemoryBreakdownModel.CachedSnapshotDataIndex.Type.ManagedType:
                {
                    var managedTypeIndex = dataIndex.Index;
                    selection = MemorySampleSelection.FromManagedTypeIndex(managedTypeIndex);
                    break;
                }

                case AllTrackedMemoryBreakdownModel.CachedSnapshotDataIndex.Type.Invalid:
                default:
                    break;
            }

            var window = EditorWindow.GetWindow<MemoryProfilerWindow>();
            if (!selection.Equals(MemorySampleSelection.InvalidMainSelection))
                window.UIState.RegisterSelectionChangeEvent(selection);
            else
                window.UIState.ClearSelection(MemorySampleSelectionRank.MainSelection);
        }

        IEnumerable<AllTrackedMemoryBreakdownModel.SortDescriptor> BuildSortDescriptorsFromTreeView()
        {
            var sortDescriptors = new List<AllTrackedMemoryBreakdownModel.SortDescriptor>();

            var sortedColumns = m_TreeView.sortedColumns;
            using (var enumerator = sortedColumns.GetEnumerator())
            {
                if (enumerator.MoveNext())
                {
                    var sortDescription = enumerator.Current;
                    var sortProperty = ColumnNameToSortbleItemDataProperty(sortDescription.columnName);
                    var sortDirection = (sortDescription.direction == SortDirection.Ascending) ?
                        AllTrackedMemoryBreakdownModel.SortDirection.Ascending : AllTrackedMemoryBreakdownModel.SortDirection.Descending;
                    var sortDescriptor = new AllTrackedMemoryBreakdownModel.SortDescriptor(sortProperty, sortDirection);
                    sortDescriptors.Add(sortDescriptor);
                }
            }

            return sortDescriptors;
        }

        AllTrackedMemoryBreakdownModel.SortableItemDataProperty ColumnNameToSortbleItemDataProperty(string columnName)
        {
            switch (columnName)
            {
                case k_UxmlIdentifier_TreeViewColumn__Description:
                    return AllTrackedMemoryBreakdownModel.SortableItemDataProperty.Name;

                case k_UxmlIdentifier_TreeViewColumn__Size:
                case k_UxmlIdentifier_TreeViewColumn__SizeBar:
                    return AllTrackedMemoryBreakdownModel.SortableItemDataProperty.Size;

                default:
                    throw new ArgumentException("Unable to sort. Unknown column name.");
            }
        }
    }
}
#endif
