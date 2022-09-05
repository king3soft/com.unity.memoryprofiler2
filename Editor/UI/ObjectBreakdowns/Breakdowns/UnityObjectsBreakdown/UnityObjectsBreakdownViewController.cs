#if UNITY_2022_1_OR_NEWER
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Unity.MemoryProfiler.Editor.UI
{
    class UnityObjectsBreakdownViewController : ViewController
    {
        public static string GetBreakdownName(bool showDuplicatedOnly = false) => "Unity Objects";
        string BreakdownName => GetBreakdownName(m_ShowDuplicatesOnly);
        const string k_UxmlAssetGuid = "064063783ea00e34f8655d38229f59e3";
        const string k_UssClass_Dark = "unity-objects-breakdown-view__dark";
        const string k_UssClass_Light = "unity-objects-breakdown-view__light";
        const string k_UxmlIdentifier_SearchField = "unity-objects-breakdown-view__search-field";
        const string k_UxmlIdentifier_TotalBar = "unity-objects-breakdown-view__total__bar";
        const string k_UxmlIdentifier_TotalInTableLabel = "unity-objects-breakdown-view__total-footer__table-label";
        const string k_UxmlIdentifier_TotalInSnapshotLabel = "unity-objects-breakdown-view__total-footer__snapshot-label";
        const string k_UxmlIdentifier_TreeView = "unity-objects-breakdown-view__tree-view";
        const string k_UxmlIdentifier_TreeViewColumn__Description = "unity-objects-breakdown-view__tree-view__column__description";
        const string k_UxmlIdentifier_TreeViewColumn__Size = "unity-objects-breakdown-view__tree-view__column__size";
        const string k_UxmlIdentifier_TreeViewColumn__SizeBar = "unity-objects-breakdown-view__tree-view__column__size-bar";
        const string k_UxmlIdentifier_TreeViewColumn__NativeSize = "unity-objects-breakdown-view__tree-view__column__native-size";
        const string k_UxmlIdentifier_TreeViewColumn__ManagedSize = "unity-objects-breakdown-view__tree-view__column__managed-size";
        const string k_UxmlIdentifier_FlattenToggle = "unity-objects-breakdown-view__toolbar__flatten-toggle";
        const string k_UxmlIdentifier_LoadingOverlay = "unity-objects-breakdown-view__loading-overlay";
        const string k_UxmlIdentifier_ErrorLabel = "unity-objects-breakdown-view__error-label";
        const string k_ErrorMessage = "Snapshot is from an outdated Unity version that is not fully supported.";

        // Model.
        readonly CachedSnapshot m_Snapshot;
        UnityObjectsBreakdownModel m_Model;
        AsyncWorker<UnityObjectsBreakdownModel> m_BuildModelWorker;
        bool m_ShowDuplicatesOnly;

        // View.
        ToolbarSearchField m_SearchField;
        ProgressBar m_TotalBar;
        Label m_TotalInTableLabel;
        Label m_TotalInSnapshotLabel;
        MultiColumnTreeView m_TreeView;
        Toggle m_FlattenToggle;
        ActivityIndicatorOverlay m_LoadingOverlay;
        Label m_ErrorLabel;

        public UnityObjectsBreakdownViewController(CachedSnapshot snapshot) : this(snapshot, false) {}

        // We are trialing Duplicates as an entirely separate breakdown. For now we use the exact same UI as Unity Objects but adjust a flag on the backend.
        public UnityObjectsBreakdownViewController(CachedSnapshot snapshot, bool showDuplicatesOnly)
        {
            m_Snapshot = snapshot;
            // in 0.7.0 we have a separate view for Duplicates that is gone in 1.0.0-pre.1 where this is just a checkbox
            // As a stand in for analytics, we emit an event here if duplication filter is on
            // We ignore it if it is off, as that is the default, and we don't have a good way to know if it was before without increasing the intrusion of this change
            // for 1.0.0-pre.1, just remove the analytics call here
            if (showDuplicatesOnly)
                MemoryProfilerAnalytics.AddInteractionCountToEvent<MemoryProfilerAnalytics.InteractionsInPage, MemoryProfilerAnalytics.PageInteractionType>(
                    MemoryProfilerAnalytics.PageInteractionType.DuplicateFilterWasApplied);
            m_ShowDuplicatesOnly = showDuplicatesOnly;
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

            m_FlattenToggle.text = "Flatten Hierarchy";
            m_FlattenToggle.RegisterValueChangedCallback(SetHierarchyFlattened);
            m_SearchField.RegisterValueChangedCallback(SearchBreakdown);
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
            m_FlattenToggle = View.Q<Toggle>(k_UxmlIdentifier_FlattenToggle);
            m_LoadingOverlay = View.Q<ActivityIndicatorOverlay>(k_UxmlIdentifier_LoadingOverlay);
            m_ErrorLabel = View.Q<Label>(k_UxmlIdentifier_ErrorLabel);
        }

        void ConfigureTreeView()
        {
            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__Description, "Description", BindCellForDescriptionColumn(), UnityObjectsDescriptionCell.Instantiate);
            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__Size, "Total Size", BindCellForSizeColumn(SizeType.Total));
            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__SizeBar, "Total Size Bar", BindCellForSizeBarColumn(), MakeSizeBarCell);
            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__NativeSize, "Native Size", BindCellForSizeColumn(SizeType.Native));
            ConfigureTreeViewColumn(k_UxmlIdentifier_TreeViewColumn__ManagedSize, "Managed Size", BindCellForSizeColumn(SizeType.Managed));

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
            var unityObjectNameFilter = m_SearchField.value;
            var flatten = m_FlattenToggle.value;
            var potentialDuplicatesFilter = m_ShowDuplicatesOnly;
            var args = new UnityObjectsBreakdownModelBuilder.BuildArgs(unityObjectNameFilter, flatten, potentialDuplicatesFilter);
            var sortDescriptors = BuildSortDescriptorsFromTreeView();
            m_BuildModelWorker = new AsyncWorker<UnityObjectsBreakdownModel>();
            m_BuildModelWorker.Execute(() =>
            {
                try
                {
                    // Build the data model.
                    var modelBuilder = new UnityObjectsBreakdownModelBuilder();
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
                var cell = (UnityObjectsDescriptionCell)element;
                var itemData = m_TreeView.GetItemDataForIndex<UnityObjectsBreakdownModel.ItemData>(rowIndex);

                var itemTypeNames = m_Model.ItemTypeNamesMap;
                itemTypeNames.TryGetValue(itemData.TypeNameLookupKey, out var typeName);
                cell.SetIconForTypeName(typeName);

                var displayText = itemData.Name;
                if (string.IsNullOrEmpty(displayText))
                    displayText = k_NoName;
                cell.SetText(displayText);

                var secondaryDisplayText = string.Empty;
                var childCount = itemData.ChildCount;
                if (childCount > 0)
                    secondaryDisplayText = $"({childCount} Object{((childCount > 1) ? "s" : string.Empty)})";
                cell.SetSecondaryText(secondaryDisplayText);
            };
        }

        Action<VisualElement, int> BindCellForSizeBarColumn()
        {
            return (element, rowIndex) =>
            {
                var size = m_TreeView.GetItemDataForIndex<UnityObjectsBreakdownModel.ItemData>(rowIndex).TotalSize;
                var progress = (float)size / m_Model.TotalMemorySize;

                var cell = (ProgressBar)element;
                cell.SetProgress(progress);
            };
        }

        Action<VisualElement, int> BindCellForSizeColumn(SizeType sizeType)
        {
            return (element, rowIndex) =>
            {
                var itemData = m_TreeView.GetItemDataForIndex<UnityObjectsBreakdownModel.ItemData>(rowIndex);
                var size = 0UL;
                switch (sizeType)
                {
                    case SizeType.Total:
                        size = itemData.TotalSize;
                        break;

                    case SizeType.Native:
                        size = itemData.NativeSize;
                        break;

                    case SizeType.Managed:
                        size = itemData.ManagedSize;
                        break;

                    case SizeType.Gpu:
                        size = itemData.GpuSize;
                        break;

                    default:
                        throw new ArgumentException("Unknown size type.");
                }
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

        void SearchBreakdown(ChangeEvent<string> evt)
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

        void SetHierarchyFlattened(ChangeEvent<bool> evt)
        {
            BuildModelAsync();

            MemoryProfilerAnalytics.AddInteractionCountToEvent<MemoryProfilerAnalytics.InteractionsInPage, MemoryProfilerAnalytics.PageInteractionType>(
                evt.newValue ? MemoryProfilerAnalytics.PageInteractionType.TreeViewWasFlattened : MemoryProfilerAnalytics.PageInteractionType.TreeViewWasUnflattened);
        }

        void OnTreeViewSelectionChanged(IEnumerable<object> items)
        {
            var selectedIndex = m_TreeView.selectedIndex;
            var data = m_TreeView.GetItemDataForIndex<UnityObjectsBreakdownModel.ItemData>(selectedIndex);
            var dataIndex = data.DataIndex;

            // Temporary integration with legacy history/selection API to show UI for some selected items in the Details view.
            var selection = MemorySampleSelection.InvalidMainSelection;
            switch (dataIndex.DataType)
            {
                case UnityObjectsBreakdownModel.CachedSnapshotDataIndex.Type.NativeObject:
                {
                    // Convert to a unified object to get Unified Object UI in Details view.
                    var nativeObjectIndex = dataIndex.Index;
                    var unifiedObjectIndex = ObjectData.FromNativeObjectIndex(
                        m_Snapshot,
                        Convert.ToInt32(nativeObjectIndex))
                        .GetUnifiedObjectIndex(m_Snapshot);
                    selection = MemorySampleSelection.FromUnifiedObjectIndex(unifiedObjectIndex);
                    break;
                }

                case UnityObjectsBreakdownModel.CachedSnapshotDataIndex.Type.NativeType:
                {
                    var nativeTypeIndex = dataIndex.Index;
                    selection = MemorySampleSelection.FromNativeTypeIndex(nativeTypeIndex);
                    break;
                }

                case UnityObjectsBreakdownModel.CachedSnapshotDataIndex.Type.Invalid:
                default:
                    break;
            }

            var window = EditorWindow.GetWindow<MemoryProfilerWindow>();
            if (!selection.Equals(MemorySampleSelection.InvalidMainSelection))
                window.UIState.RegisterSelectionChangeEvent(selection);
            else
                window.UIState.ClearSelection(MemorySampleSelectionRank.MainSelection);
        }

        IEnumerable<UnityObjectsBreakdownModel.SortDescriptor> BuildSortDescriptorsFromTreeView()
        {
            var sortDescriptors = new List<UnityObjectsBreakdownModel.SortDescriptor>();

            var sortedColumns = m_TreeView.sortedColumns;
            using (var enumerator = sortedColumns.GetEnumerator())
            {
                if (enumerator.MoveNext())
                {
                    var sortDescription = enumerator.Current;
                    var sortProperty = ColumnNameToSortbleItemDataProperty(sortDescription.columnName);
                    var sortDirection = (sortDescription.direction == SortDirection.Ascending) ?
                        UnityObjectsBreakdownModel.SortDirection.Ascending : UnityObjectsBreakdownModel.SortDirection.Descending;
                    var sortDescriptor = new UnityObjectsBreakdownModel.SortDescriptor(sortProperty, sortDirection);
                    sortDescriptors.Add(sortDescriptor);
                }
            }

            return sortDescriptors;
        }

        UnityObjectsBreakdownModel.SortableItemDataProperty ColumnNameToSortbleItemDataProperty(string columnName)
        {
            switch (columnName)
            {
                case k_UxmlIdentifier_TreeViewColumn__Description:
                    return UnityObjectsBreakdownModel.SortableItemDataProperty.Name;

                case k_UxmlIdentifier_TreeViewColumn__Size:
                case k_UxmlIdentifier_TreeViewColumn__SizeBar:
                    return UnityObjectsBreakdownModel.SortableItemDataProperty.TotalSize;

                case k_UxmlIdentifier_TreeViewColumn__NativeSize:
                    return UnityObjectsBreakdownModel.SortableItemDataProperty.NativeSize;

                case k_UxmlIdentifier_TreeViewColumn__ManagedSize:
                    return UnityObjectsBreakdownModel.SortableItemDataProperty.ManagedSize;

                default:
                    throw new ArgumentException("Unable to sort. Unknown column name.");
            }
        }

        enum SizeType
        {
            Total,
            Native,
            Managed,
            Gpu,
        }
    }
}
#endif
