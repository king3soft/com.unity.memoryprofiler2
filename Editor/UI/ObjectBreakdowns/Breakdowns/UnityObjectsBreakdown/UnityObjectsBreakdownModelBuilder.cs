#if UNITY_2022_1_OR_NEWER
using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Unity.MemoryProfiler.Editor.UI
{
    // Builds a UnityObjectsBreakdownModel.
    class UnityObjectsBreakdownModelBuilder
    {
        int m_ItemId;

        public UnityObjectsBreakdownModel Build(CachedSnapshot snapshot, in BuildArgs args)
        {
            if (!CanBuildBreakdownForSnapshot(snapshot))
                throw new ArgumentException("Unsupported snapshot version.", nameof(snapshot));

            var rootNodes = BuildUnityObjectsGroupedByType(snapshot, args, out var itemTypeNamesMap);
            if (args.FlattenHierarchy)
                rootNodes = TreeModelUtility.RetrieveLeafNodesOfTree(rootNodes);

            var totalSnapshotMemorySize = snapshot.MetaData.TargetMemoryStats.Value.TotalVirtualMemory;
            var model = new UnityObjectsBreakdownModel(rootNodes, itemTypeNamesMap, totalSnapshotMemorySize);
            return model;
        }

        bool CanBuildBreakdownForSnapshot(CachedSnapshot snapshot)
        {
            // TargetAndMemoryInfo is required to obtain the total snapshot memory size.
            if (!snapshot.HasTargetAndMemoryInfo)
                return false;

            return true;
        }

        List<TreeViewItemData<UnityObjectsBreakdownModel.ItemData>> BuildUnityObjectsGroupedByType(
            CachedSnapshot snapshot,
            in BuildArgs args,
            out Dictionary<int, string> outItemTypeNamesMap)
        {
            // Build a map of Type-Index to Unity-Objects.
            var typeIndexToTypeObjectsMap = new Dictionary<int, List<TreeViewItemData<UnityObjectsBreakdownModel.ItemData>>>();
            var nativeObjects = snapshot.NativeObjects;
            for (var i = 0L; i < nativeObjects.Count; i++)
            {
                // Filter by Unity-Object-Name. Skip objects that don't pass the name filter.
                var nativeObjectName = nativeObjects.ObjectName[i];
                if (!UnityObjectPassesNameFilter(nativeObjectName, args.UnityObjectNameFilter))
                    continue;

                // Get native object size.
                var nativeObjectSize = nativeObjects.Size[i];

                // Get managed object size, if necessary.
                var managedObjectSize = 0UL;
                var managedObjectIndex = nativeObjects.ManagedObjectIndex[i];
                if (managedObjectIndex >= 0)
                {
                    // This native object is linked to a managed object. Count the managed object's size.
                    var managedObject = snapshot.CrawledData.ManagedObjects[managedObjectIndex];
                    managedObjectSize = Convert.ToUInt64(managedObject.Size);
                }

                // Get GPU object size, if necessary.
                var gpuObjectSize = 0UL;
                {
                    // TODO
                }

                // Store the type index so an item can look-up its type name without storing it per-item.
                var typeIndex = nativeObjects.NativeTypeArrayIndex[i];
                var typeNameLookupKey = typeIndex;

                // Store the native object index.
                var nativeObjectDataIndex = UnityObjectsBreakdownModel.CachedSnapshotDataIndex.FromNativeObjectIndex(i);

                // Create node for conceptual Unity Object.
                var name = nativeObjects.ObjectName[i];
                var item = new TreeViewItemData<UnityObjectsBreakdownModel.ItemData>(
                    m_ItemId++,
                    new UnityObjectsBreakdownModel.ItemData(
                        name,
                        nativeObjectSize,
                        managedObjectSize,
                        gpuObjectSize,
                        typeNameLookupKey,
                        nativeObjectDataIndex)
                    );

                // Add node to corresponding type's list of Unity Objects.
                if (typeIndexToTypeObjectsMap.TryGetValue(typeIndex, out var typeObjects))
                    typeObjects.Add(item);
                else
                    typeIndexToTypeObjectsMap.Add(typeIndex, new List<TreeViewItemData<UnityObjectsBreakdownModel.ItemData>>() { item });
            }

            // Filter by potential duplicates, if necessary.
            if (args.PotentialDuplicatesFilter)
                typeIndexToTypeObjectsMap = FilterTypeIndexToTypeObjectsMapForPotentialDuplicatesAndGroup(typeIndexToTypeObjectsMap);

            // Build a tree of Unity Objects, grouped by Unity Object Type, from the map.
            var unityObjectsTree = new List<TreeViewItemData<UnityObjectsBreakdownModel.ItemData>>(typeIndexToTypeObjectsMap.Count);
            outItemTypeNamesMap = new Dictionary<int, string>(typeIndexToTypeObjectsMap.Count);
            var nativeTypes = snapshot.NativeTypes;
            foreach (var kvp in typeIndexToTypeObjectsMap)
            {
                var typeIndex = kvp.Key;
                var typeObjects = kvp.Value;

                // Calculate the total size of the Unity Object Type by summing all of its Unity Objects.
                var typeNativeSize = 0UL;
                var typeManagedSize = 0UL;
                var typeGpuSize = 0UL;
                foreach (var typeObject in typeObjects)
                {
                    typeNativeSize += typeObject.data.NativeSize;
                    typeManagedSize += typeObject.data.ManagedSize;
                    typeGpuSize += typeObject.data.GpuSize;
                }

                // Store type names in a map, keyed off type index. Each item can use this map to look-up its type name without storing it per-item.
                var typeName = nativeTypes.TypeName[typeIndex];
                outItemTypeNamesMap.Add(typeIndex, typeName);

                // Store the native type index.
                var nativeTypeDataIndex = UnityObjectsBreakdownModel.CachedSnapshotDataIndex.FromNativeTypeIndex(typeIndex);

                // Create node for Unity Object Type.
                var node = new TreeViewItemData<UnityObjectsBreakdownModel.ItemData>(
                    m_ItemId++,
                    new UnityObjectsBreakdownModel.ItemData(
                        typeName,
                        typeNativeSize,
                        typeManagedSize,
                        typeGpuSize,
                        typeIndex,
                        nativeTypeDataIndex,
                        typeObjects.Count),
                    typeObjects);
                unityObjectsTree.Add(node);
            }

            return unityObjectsTree;
        }

        bool UnityObjectPassesNameFilter(string unityObjectName, string unityObjectNameFilter)
        {
            if (!string.IsNullOrEmpty(unityObjectNameFilter))
            {
                if (string.IsNullOrEmpty(unityObjectName))
                    return false;

                if (!unityObjectName.Contains(unityObjectNameFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        // Filter the map for potential duplicates. These are objects with the same type, name, and size. Group duplicates under a single item.
        Dictionary<int, List<TreeViewItemData<UnityObjectsBreakdownModel.ItemData>>> FilterTypeIndexToTypeObjectsMapForPotentialDuplicatesAndGroup(Dictionary<int, List<TreeViewItemData<UnityObjectsBreakdownModel.ItemData>>> typeIndexToTypeObjectsMap)
        {
            var filteredTypeIndexToTypeObjectsMap = new Dictionary<int, List<TreeViewItemData<UnityObjectsBreakdownModel.ItemData>>>();

            foreach (var typeIndexToTypeObjectsKvp in typeIndexToTypeObjectsMap)
            {
                // Break type objects into separate lists based on name & size.
                var typeObjects = typeIndexToTypeObjectsKvp.Value;
                var potentialDuplicateObjectsMap = new Dictionary<Tuple<string, ulong>, List<TreeViewItemData<UnityObjectsBreakdownModel.ItemData>>>();
                foreach (var typeObject in typeObjects)
                {
                    var data = typeObject.data;
                    var nameSizeTuple = new Tuple<string, ulong>(data.Name, data.TotalSize);
                    if (potentialDuplicateObjectsMap.TryGetValue(nameSizeTuple, out var nameSizeTypeObjects))
                        nameSizeTypeObjects.Add(typeObject);
                    else
                        potentialDuplicateObjectsMap.Add(nameSizeTuple, new List<TreeViewItemData<UnityObjectsBreakdownModel.ItemData>>() { typeObject });
                }

                // Create potential duplicate groups for lists that contain more than one item (duplicates).
                var potentialDuplicateItems = new List<TreeViewItemData<UnityObjectsBreakdownModel.ItemData>>();
                var typeIndex = typeIndexToTypeObjectsKvp.Key;
                foreach (var potentialDuplicateObjectsKvp in potentialDuplicateObjectsMap)
                {
                    var potentialDuplicateObjects = potentialDuplicateObjectsKvp.Value;
                    if (potentialDuplicateObjects.Count > 1)
                    {
                        var potentialDuplicateData = potentialDuplicateObjects[0].data;
                        
                        var duplicateCount = 0;
                        var potentialDuplicatesNativeSize = 0UL;
                        var potentialDuplicatesManagedSize = 0UL;
                        var potentialDuplicatesGpuSize = 0UL;
                        while (duplicateCount < potentialDuplicateObjects.Count)
                        {
                            potentialDuplicatesNativeSize += potentialDuplicateData.NativeSize;
                            potentialDuplicatesManagedSize += potentialDuplicateData.ManagedSize;
                            potentialDuplicatesGpuSize += potentialDuplicateData.GpuSize;

                            duplicateCount++;
                        }

                        // Show native type on selection of duplicate for now. Ideally we would have bespoke details view for duplicates.
                        var nativeTypeDataIndex = UnityObjectsBreakdownModel.CachedSnapshotDataIndex.FromNativeTypeIndex(typeIndex);

                        var potentialDuplicateItem = new TreeViewItemData<UnityObjectsBreakdownModel.ItemData>(
                            m_ItemId++,
                            new UnityObjectsBreakdownModel.ItemData(
                                potentialDuplicateData.Name,
                                potentialDuplicatesNativeSize,
                                potentialDuplicatesManagedSize,
                                potentialDuplicatesGpuSize,
                                potentialDuplicateData.TypeNameLookupKey,
                                nativeTypeDataIndex,
                                potentialDuplicateObjects.Count),
                            potentialDuplicateObjects);
                        potentialDuplicateItems.Add(potentialDuplicateItem);
                    }
                }

                // Add list containing duplicate type objects to corresponding type index in filtered map.
                if (potentialDuplicateItems.Count > 0)
                    filteredTypeIndexToTypeObjectsMap.Add(typeIndex, potentialDuplicateItems);
            }

            return filteredTypeIndexToTypeObjectsMap;
        }

        internal readonly struct BuildArgs
        {
            public BuildArgs(string unityObjectNameFilter) : this(unityObjectNameFilter, false, false) { }

            public BuildArgs(string unityObjectNameFilter, bool flattenHierarchy, bool potentialDuplicatesFilter)
            {
                UnityObjectNameFilter = unityObjectNameFilter;
                FlattenHierarchy = flattenHierarchy;
                PotentialDuplicatesFilter = potentialDuplicatesFilter;
            }

            public string UnityObjectNameFilter { get; }

            public bool FlattenHierarchy { get; }

            public bool PotentialDuplicatesFilter { get; }
        }
    }
}
#endif
