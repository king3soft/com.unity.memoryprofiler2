#if UNITY_2022_1_OR_NEWER
using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Unity.MemoryProfiler.Editor.UI
{
    // Builds an AllTrackedMemoryBreakdownModel.
    class AllTrackedMemoryBreakdownModelBuilder
    {
        int m_ItemId;

        public AllTrackedMemoryBreakdownModel Build(CachedSnapshot snapshot, in BuildArgs args)
        {
            if (!CanBuildBreakdownForSnapshot(snapshot))
                throw new ArgumentException("Unsupported snapshot version.", nameof(snapshot));

            var rootNodes = BuildAllTrackedMemoryBreakdown(snapshot, args);
            var totalSnapshotMemorySize = snapshot.MetaData.TargetMemoryStats.Value.TotalVirtualMemory;
            var model = new AllTrackedMemoryBreakdownModel(rootNodes, totalSnapshotMemorySize);
            return model;
        }

        bool CanBuildBreakdownForSnapshot(CachedSnapshot snapshot)
        {
            // TargetAndMemoryInfo is required to obtain the total snapshot memory size and reserved sizes.
            if (!snapshot.HasTargetAndMemoryInfo)
                return false;

            return true;
        }

        List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>> BuildAllTrackedMemoryBreakdown(
            CachedSnapshot snapshot,
            in BuildArgs args)
        {
            var rootItems = new List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>();

            if (TryBuildNativeMemoryTree(snapshot, args, out var nativeMemoryTree))
                rootItems.Add(nativeMemoryTree);

            if (TryBuildScriptingMemoryTree(snapshot, args, out var scriptingMemoryTree))
                rootItems.Add(scriptingMemoryTree);

            if (TryBuildGraphicsMemoryTree(snapshot, args, out var graphicsMemoryTree))
                rootItems.Add(graphicsMemoryTree);

            if (TryBuildExecutableAndDllsTree(snapshot, args, out var codeTree))
                rootItems.Add(codeTree);

            return rootItems;
        }

        bool TryBuildNativeMemoryTree(
            CachedSnapshot snapshot,
            in BuildArgs args,
            out TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData> tree)
        {
            var accountedSize = 0UL;
            List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>> nativeItems = null;

            // Native Objects and Allocation Roots.
            {
                var nativeRootReferences = snapshot.NativeRootReferences;
                BuildNativeObjectsAndRootsTreeItems(snapshot,
                    args,
                    out accountedSize,
                    (long rootId) =>
                {
                    return nativeRootReferences.IdToIndex.TryGetValue(rootId, out var rootIndex) ? nativeRootReferences.AccumulatedSize[rootIndex] : 0;
                }, ref nativeItems);
            }

            // Native Temporary Allocators.
            {
                // They represent native heap which is not associated with any Unity object or native root
                // and thus can be represented in isolation.
                // Currently all temp allocators has "TEMP" string in their names.
                if (snapshot.HasGfxResourceReferencesAndAllocators)
                {
                    var nativeAllocatorsTotalSize = 0UL;

                    var nativeAllocators = snapshot.NativeAllocators;
                    var nativeAllocatorItems = new List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>();

                    for (int i = 0; i != nativeAllocators.Count; ++i)
                    {
                        string allocatorName = nativeAllocators.AllocatorName[i];
                        if (!allocatorName.Contains("TEMP"))
                            continue;

                        // Filter by Native Allocator name. Skip objects that don't pass the name filter.
                        if (!NamePassesFilter(allocatorName, args.NameFilter))
                            continue;

                        var size = nativeAllocators.ReservedSize[i];
                        var allocatorItem = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                            m_ItemId++,
                            new AllTrackedMemoryBreakdownModel.ItemData(
                                allocatorName,
                                size)
                            );
                        nativeAllocatorItems.Add(allocatorItem);

                        nativeAllocatorsTotalSize += size;
                    }

                    accountedSize += nativeAllocatorsTotalSize;

                    if (nativeAllocatorItems.Count > 0)
                    {
                        var nativeAllocatorsItemData = new AllTrackedMemoryBreakdownModel.ItemData("Native Temporary Allocators", nativeAllocatorsTotalSize, nativeAllocatorItems.Count);
                        var nativeAllocatorsItem = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(m_ItemId++, nativeAllocatorsItemData, nativeAllocatorItems);
                        nativeItems.Add(nativeAllocatorsItem);
                    }
                }
            }

            // Reserved
            {
                // Only add 'Reserved' item if not applying a filter.
                if (string.IsNullOrEmpty(args.NameFilter))
                {
                    var memoryStats = snapshot.MetaData.TargetMemoryStats.Value;
                    var totalSize = memoryStats.TotalReservedMemory - memoryStats.GraphicsUsedMemory - memoryStats.GcHeapReservedMemory;
                    if (totalSize > accountedSize)
                    {
                        var remainingNativeMemory = totalSize - accountedSize;
                        var item = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                            m_ItemId++,
                            new AllTrackedMemoryBreakdownModel.ItemData(
                                "Reserved",
                                remainingNativeMemory)
                            );
                        nativeItems.Add(item);

                        accountedSize += remainingNativeMemory;
                    }
                }
            }

            // Total Native Heap.
            if (nativeItems.Count > 0)
            {
                tree = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                    m_ItemId++,
                    new AllTrackedMemoryBreakdownModel.ItemData(
                        "Native Memory",
                        accountedSize),
                    nativeItems);
                return true;
            }

            tree = default;
            return false;
        }

        void BuildNativeObjectsAndRootsTreeItems(
            CachedSnapshot snapshot,
            in BuildArgs args,
            out ulong totalHeapSize,
            Func<long, ulong> rootIdToSizeFunc,
            ref List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>> nativeItems)
        {
            totalHeapSize = 0UL;
            if (nativeItems == null)
                nativeItems = new List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>();
            var cachedAccountedNativeObjects = new Dictionary<long, long>();

            // Native Objects grouped by Native Type.
            {
                // Build type-index to type-objects map.
                var typeIndexToTypeObjectsMap = new Dictionary<int, List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>>();
                var nativeObjectsTotalSize = 0UL;
                var nativeObjects = snapshot.NativeObjects;
                var nativeObjectsCountInt32 = Convert.ToInt32(nativeObjects.Count);
                cachedAccountedNativeObjects.EnsureCapacity(nativeObjectsCountInt32);
                for (var i = 0L; i < nativeObjects.Count; i++)
                {
                    // Mark this object as visited for later roots iteration.
                    var rootId = nativeObjects.RootReferenceId[i];
                    cachedAccountedNativeObjects.Add(rootId, i);
                    ulong size = rootIdToSizeFunc(rootId);
                    // Ignore empty objects.
                    if (size == 0)
                        continue;

                    // Filter by Native Object name. Skip objects that don't pass the name filter.
                    var name = nativeObjects.ObjectName[i];
                    if (!NamePassesFilter(name, args.NameFilter))
                        continue;

                    // Store the native object index.
                    var nativeObjectDataIndex = AllTrackedMemoryBreakdownModel.CachedSnapshotDataIndex.FromNativeObjectIndex(i);

                    // Create item for native object.
                    var item = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                        m_ItemId++,
                        new AllTrackedMemoryBreakdownModel.ItemData(
                            name,
                            size,
                            dataIndex: nativeObjectDataIndex)
                        );

                    // Add object to corresponding type entry in map.
                    var typeIndex = nativeObjects.NativeTypeArrayIndex[i];
                    if (typeIndexToTypeObjectsMap.TryGetValue(typeIndex, out var typeObjects))
                        typeObjects.Add(item);
                    else
                        typeIndexToTypeObjectsMap.Add(typeIndex, new List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>() { item });
                }

                // Build type-objects tree from map.
                var nativeTypes = snapshot.NativeTypes;
                var nativeTypeItems = new List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>(typeIndexToTypeObjectsMap.Count);
                foreach (var kvp in typeIndexToTypeObjectsMap)
                {
                    var typeIndex = kvp.Key;
                    var typeObjects = kvp.Value;

                    // Calculate type size from all its objects.
                    var typeSize = 0UL;
                    foreach (var typeObject in typeObjects)
                        typeSize += typeObject.data.Size;

                    // Ignore empty types.
                    if (typeSize == 0)
                        continue;

                    // Store the native type index.
                    var nativeTypeDataIndex = AllTrackedMemoryBreakdownModel.CachedSnapshotDataIndex.FromNativeTypeIndex(typeIndex);

                    var typeName = nativeTypes.TypeName[typeIndex];
                    var typeItem = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                        m_ItemId++,
                        new AllTrackedMemoryBreakdownModel.ItemData(
                            typeName,
                            typeSize,
                            typeObjects.Count,
                            nativeTypeDataIndex),
                        typeObjects);
                    nativeTypeItems.Add(typeItem);

                    // Accumulate type's size into total size of native objects.
                    nativeObjectsTotalSize += typeSize;
                }

                totalHeapSize += nativeObjectsTotalSize;

                if (nativeTypeItems.Count > 0)
                {
                    var nativeObjectsItemData = new AllTrackedMemoryBreakdownModel.ItemData("Unity Objects", nativeObjectsTotalSize, nativeTypeItems.Count);
                    var nativeObjectsItem = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(m_ItemId++, nativeObjectsItemData, nativeTypeItems);
                    nativeItems.Add(nativeObjectsItem);
                }
            }

            // Native Roots
            {
                var nativeRootsTotalSize = 0UL;
                var nativeRootReferences = snapshot.NativeRootReferences;
                var nativeRootAreaItems = new List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>();

                // Get indices of all roots grouped by area.
                var rootAreas = new Dictionary<string, List<long>>();
                // Start from index 1 as 0 is executable and dll size!
                for (var i = 1; i < nativeRootReferences.Count; ++i)
                {
                    var accounted = cachedAccountedNativeObjects.TryGetValue(nativeRootReferences.Id[i], out _);
                    if (accounted)
                        continue;

                    var rootAreaName = nativeRootReferences.AreaName[i];
                    if (rootAreas.TryGetValue(rootAreaName, out var rootIndices))
                        rootIndices.Add(i);
                    else
                        rootAreas.Add(rootAreaName, new List<long>() { i });
                }

                // Build tree for roots per area.
                foreach (KeyValuePair<string, List<long>> kvp in rootAreas)
                {
                    var nativeRootAreaTotalSize = 0UL;
                    var rootAreaItems = new List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>();
                    var rootIndices = kvp.Value;
                    foreach (var index in rootIndices)
                    {
                        var size = rootIdToSizeFunc(nativeRootReferences.Id[index]);
                        // Ignore empty roots.
                        if (size == 0)
                            continue;

                        // Filter by Native Root Reference object name. Skip objects that don't pass the name filter.
                        var objectName = nativeRootReferences.ObjectName[index];
                        if (!NamePassesFilter(objectName, args.NameFilter))
                            continue;

                        var nativeRootItem = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                            m_ItemId++,
                            new AllTrackedMemoryBreakdownModel.ItemData(
                                objectName,
                                size)
                            );
                        rootAreaItems.Add(nativeRootItem);

                        // Accumulate the root's size in its area's total.
                        nativeRootAreaTotalSize += size;
                    }

                    // Ignore empty areas.
                    if (nativeRootAreaTotalSize == 0)
                        continue;

                    var nativeRootAreaName = kvp.Key;
                    var nativeRootAreaItem = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                        m_ItemId++,
                        new AllTrackedMemoryBreakdownModel.ItemData(nativeRootAreaName, nativeRootAreaTotalSize, rootAreaItems.Count),
                        rootAreaItems);
                    nativeRootAreaItems.Add(nativeRootAreaItem);

                    nativeRootsTotalSize += nativeRootAreaTotalSize;
                }

                if (nativeRootAreaItems.Count > 0)
                {
                    var nativeRootsItemData = new AllTrackedMemoryBreakdownModel.ItemData("Unity Subsystems", nativeRootsTotalSize, nativeRootAreaItems.Count);
                    var nativeRootsItem = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(m_ItemId++, nativeRootsItemData, nativeRootAreaItems);
                    nativeItems.Add(nativeRootsItem);
                }

                totalHeapSize += nativeRootsTotalSize;
            }
        }

        bool TryBuildScriptingMemoryTree(
            CachedSnapshot snapshot,
            in BuildArgs args,
            out TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData> tree)
        {
            var accountedSize = 0UL;
            var scriptingItems = new List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>();

            // Empty Heap Space
            {
                const string k_EmptyHeapSpaceName = "Empty Heap Space";
                if (NamePassesFilter(k_EmptyHeapSpaceName, args.NameFilter))
                {
                    var emptyHeapSpace = snapshot.ManagedHeapSections.ManagedHeapMemoryReserved - snapshot.CrawledData.ManagedObjectMemoryUsage;
                    var item = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                        m_ItemId++,
                        new AllTrackedMemoryBreakdownModel.ItemData(
                            k_EmptyHeapSpaceName,
                            emptyHeapSpace)
                        );
                    scriptingItems.Add(item);

                    accountedSize += emptyHeapSpace;
                }
            }

            // Virtual Machine
            {
                var virtualMachineMemoryName = UIContentData.TextContent.DefaultVirtualMachineMemoryCategoryLabel;
                if (snapshot.MetaData.TargetInfo.HasValue)
                {
                    switch (snapshot.MetaData.TargetInfo.Value.ScriptingBackend)
                    {
                        case UnityEditor.ScriptingImplementation.Mono2x:
                            virtualMachineMemoryName = UIContentData.TextContent.MonoVirtualMachineMemoryCategoryLabel;
                            break;

                        case UnityEditor.ScriptingImplementation.IL2CPP:
                            virtualMachineMemoryName = UIContentData.TextContent.IL2CPPVirtualMachineMemoryCategoryLabel;
                            break;

                        case UnityEditor.ScriptingImplementation.WinRTDotNET:
                        default:
                            break;
                    }
                }

                if (NamePassesFilter(virtualMachineMemoryName, args.NameFilter))
                {
                    var virtualMachineMemoryReserved = snapshot.ManagedHeapSections.VirtualMachineMemoryReserved;
                    var item = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                        m_ItemId++,
                        new AllTrackedMemoryBreakdownModel.ItemData(
                            virtualMachineMemoryName,
                            virtualMachineMemoryReserved)
                        );
                    scriptingItems.Add(item);

                    accountedSize += virtualMachineMemoryReserved;
                }
            }

            // Objects (Grouped By Type).
            {
                // Build type-index to type-objects map.
                var managedObjectsTotalSize = 0UL;
                var managedObjects = snapshot.CrawledData.ManagedObjects;
                var typeIndexToTypeObjectsMap = new Dictionary<int, List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>>();
                for (var i = 0L; i < managedObjects.Count; i++)
                {
                    var size = Convert.ToUInt64(managedObjects[i].Size);

                    // Use native object name if possible.
                    var name = string.Empty;
                    var nativeObjectIndex = managedObjects[i].NativeObjectIndex;
                    if (nativeObjectIndex > 0)
                        name = snapshot.NativeObjects.ObjectName[nativeObjectIndex];

                    // Filter by Native Object name. Skip objects that don't pass the name filter.
                    if (!NamePassesFilter(name, args.NameFilter))
                        continue;

                    // Store the managed object index.
                    var managedObjectDataIndex = AllTrackedMemoryBreakdownModel.CachedSnapshotDataIndex.FromManagedObjectIndex(i);

                    // Create item for managed object.
                    var itemData = new AllTrackedMemoryBreakdownModel.ItemData(name, size, dataIndex: managedObjectDataIndex);
                    var item = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(m_ItemId++, itemData);

                    // Add object to corresponding type entry in map.
                    var typeIndex = managedObjects[i].ITypeDescription;
                    if (typeIndex < 0)
                        continue;

                    if (typeIndexToTypeObjectsMap.TryGetValue(typeIndex, out var typeObjects))
                        typeObjects.Add(item);
                    else
                        typeIndexToTypeObjectsMap.Add(typeIndex, new List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>() { item });
                }

                // Build type-objects tree from map.
                var managedTypes = snapshot.TypeDescriptions;
                var managedTypeItems = new List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>(typeIndexToTypeObjectsMap.Count);
                foreach (var kvp in typeIndexToTypeObjectsMap)
                {
                    var typeIndex = kvp.Key;
                    var typeObjects = kvp.Value;

                    // Calculate type size from all its objects.
                    var typeSize = 0UL;
                    foreach (var typeObject in typeObjects)
                        typeSize += typeObject.data.Size;

                    // Store the managed type index.
                    var managedTypeDataIndex = AllTrackedMemoryBreakdownModel.CachedSnapshotDataIndex.FromManagedTypeIndex(typeIndex);

                    var typeName = managedTypes.TypeDescriptionName[typeIndex];
                    var typeItem = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                        m_ItemId++,
                        new AllTrackedMemoryBreakdownModel.ItemData(
                            typeName,
                            typeSize,
                            typeObjects.Count,
                            managedTypeDataIndex),
                        typeObjects);
                    managedTypeItems.Add(typeItem);

                    // Accumulate type's size into total size of managed objects.
                    managedObjectsTotalSize += typeSize;
                }

                if (managedTypeItems.Count > 0)
                {
                    var item = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                        m_ItemId++,
                        new AllTrackedMemoryBreakdownModel.ItemData(
                            "Managed Objects",
                            managedObjectsTotalSize,
                            managedTypeItems.Count),
                        managedTypeItems);
                    scriptingItems.Add(item);
                }

                accountedSize += managedObjectsTotalSize;
            }

            // Reserved (Unused)
            {
                // Only add 'Reserved' item if not applying a filter.
                if (string.IsNullOrEmpty(args.NameFilter))
                {
                    var memoryStats = snapshot.MetaData.TargetMemoryStats.Value;
                    var reservedUnusedSize = memoryStats.GcHeapReservedMemory - memoryStats.GcHeapUsedMemory;
                    var item = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                        m_ItemId++,
                        new AllTrackedMemoryBreakdownModel.ItemData(
                            "Reserved (Unused)",
                            reservedUnusedSize)
                        );
                    scriptingItems.Add(item);

                    accountedSize += reservedUnusedSize;
                }
            }

            if (scriptingItems.Count > 0)
            {
                tree = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                    m_ItemId++,
                    new AllTrackedMemoryBreakdownModel.ItemData(
                        "Scripting Memory",
                        accountedSize),
                    scriptingItems);
                return true;
            }

            tree = default;
            return false;
        }

        bool TryBuildGraphicsMemoryTree(
            CachedSnapshot snapshot,
            in BuildArgs args,
            out TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData> tree)
        {
            const string k_GraphicsMemoryName = "Graphics Memory";
            var graphicsItems = new List<TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>>();

            if (snapshot.HasGfxResourceReferencesAndAllocators)
            {
                var accountedSize = 0UL;

                var nativeGfxResourceReferences = snapshot.NativeGfxResourceReferences;
                BuildNativeObjectsAndRootsTreeItems(snapshot,
                    args,
                    out accountedSize,
                    (long rootId) =>
                {
                    return nativeGfxResourceReferences.RootIdToGfxSize.TryGetValue(rootId, out var size) ? size : 0;
                }, ref graphicsItems);

                // Only add 'Reserved' item if not applying a filter.
                if (string.IsNullOrEmpty(args.NameFilter))
                {
                    var totalSize = snapshot.MetaData.TargetMemoryStats.Value.GraphicsUsedMemory;
                    if (totalSize > accountedSize)
                    {
                        var remainingGraphicsMemory = totalSize - accountedSize;
                        var item = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                            m_ItemId++,
                            new AllTrackedMemoryBreakdownModel.ItemData(
                                "Reserved",
                                remainingGraphicsMemory)
                            );
                        graphicsItems.Add(item);

                        accountedSize += remainingGraphicsMemory;
                    }
                }

                if (graphicsItems.Count > 0)
                {
                    tree = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                        m_ItemId++,
                        new AllTrackedMemoryBreakdownModel.ItemData(
                            "Graphics Memory",
                            accountedSize),
                        graphicsItems);
                    return true;
                }
            }
            else
            {
                // If we don't have graphics allocator data, display the graphics used memory on the root item.
                var graphicsUsedMemory = snapshot.MetaData.TargetMemoryStats.Value.GraphicsUsedMemory;
                if (NamePassesFilter(k_GraphicsMemoryName, args.NameFilter))
                {
                    tree = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                        m_ItemId++,
                        new AllTrackedMemoryBreakdownModel.ItemData(
                            "Graphics Memory",
                            graphicsUsedMemory),
                        graphicsItems);
                    return true;
                }
            }

            tree = default;
            return false;
        }

        bool TryBuildExecutableAndDllsTree(
            CachedSnapshot snapshot,
            in BuildArgs args,
            out TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData> tree)
        {
            const string k_ExecutableAndDllsName = "Executable And Dlls";
            if (!NamePassesFilter(k_ExecutableAndDllsName, args.NameFilter))
            {
                tree = default;
                return false;
            }

            var executableAndDllsReportedValue = snapshot.NativeRootReferences.ExecutableAndDllsReportedValue;
            tree = new TreeViewItemData<AllTrackedMemoryBreakdownModel.ItemData>(
                m_ItemId++,
                new AllTrackedMemoryBreakdownModel.ItemData(
                    k_ExecutableAndDllsName,
                    executableAndDllsReportedValue)
                );
            return true;
        }

        bool NamePassesFilter(string name, string nameFilter)
        {
            if (!string.IsNullOrEmpty(nameFilter))
            {
                if (string.IsNullOrEmpty(name))
                    return false;

                if (!name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        internal readonly struct BuildArgs
        {
            public BuildArgs(string nameFilter)
            {
                NameFilter = nameFilter;
            }

            public string NameFilter { get; }
        }
    }
}
#endif
