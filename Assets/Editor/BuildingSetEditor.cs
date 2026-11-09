using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SimpleFPS.EditorTools
{
    // Folders to fill the set from, and where buildings sharing a BuildingType disagree on weight or cooldown
    // (placement uses the largest values).
    [CustomEditor(typeof(BuildingSet))]
    public sealed class BuildingSetEditor : Editor
    {
        private void OnEnable()
        {
            BuildingSetFolders.Refresh((BuildingSet)target);
        }

        public override void OnInspectorGUI()
        {
            var set = (BuildingSet)target;
            serializedObject.Update();
            SerializedProperty folders = serializedObject.FindProperty(nameof(BuildingSet.Folders));
            EditorGUILayout.LabelField("Folders", EditorStyles.boldLabel);
            for (int i = 0; i < folders.arraySize; i++)
            {
                SerializedProperty element = folders.GetArrayElementAtIndex(i);
                var current = AssetDatabase.LoadAssetAtPath<DefaultAsset>(element.stringValue);
                EditorGUILayout.BeginHorizontal();
                var picked = (DefaultAsset)EditorGUILayout.ObjectField(current, typeof(DefaultAsset), false);
                if (picked != current)
                {
                    string path = picked != null ? AssetDatabase.GetAssetPath(picked) : "";
                    if (path == "" || AssetDatabase.IsValidFolder(path))
                        element.stringValue = path;
                }

                bool remove = GUILayout.Button("-", GUILayout.Width(22));
                EditorGUILayout.EndHorizontal();
                if (current == null && !string.IsNullOrEmpty(element.stringValue))
                    EditorGUILayout.HelpBox("Folder not found: " + element.stringValue, MessageType.Warning);
                if (remove)
                {
                    folders.DeleteArrayElementAtIndex(i);
                    break;
                }
            }

            if (GUILayout.Button("Add folder"))
            {
                folders.arraySize++;
                folders.GetArrayElementAtIndex(folders.arraySize - 1).stringValue = "";
            }

            if (serializedObject.ApplyModifiedProperties())
            {
                BuildingSetFolders.Refresh(set);
                serializedObject.Update();
            }

            bool automatic = BuildingSetFolders.HasFolders(set);
            if (automatic)
            {
                EditorGUILayout.HelpBox("Buildings is filled from these folders and their subfolders: every generated .building and every building prefab. " +
                    "A prefab made from a .building (Create editable prefab…) replaces that building. It updates whenever a building or prefab in them changes.", MessageType.Info);
                if (GUILayout.Button("Refresh now"))
                {
                    BuildingSetFolders.Refresh(set);
                    serializedObject.Update();
                }
            }

            using (new EditorGUI.DisabledScope(automatic))
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(BuildingSet.Buildings)), true);
            serializedObject.ApplyModifiedProperties();
            foreach (string mismatch in set.GetTypeMismatches())
                EditorGUILayout.HelpBox("Building type " + mismatch, MessageType.Warning);
        }
    }

    public static class BuildingSetFolders
    {
        public static bool HasFolders(BuildingSet set) => set.Folders != null && set.Folders.Any(AssetDatabase.IsValidFolder);

        // Rebuilds Buildings from the set's folders; saves the set only when the list changed.
        public static void Refresh(BuildingSet set)
        {
            if (set == null || !HasFolders(set))
                return;

            string[] folders = set.Folders.Where(AssetDatabase.IsValidFolder).ToArray();
            var paths = AssetDatabase.FindAssets("", folders).Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".building") || path.EndsWith(".prefab")).Distinct().OrderBy(path => path).ToList();
            var definitions = new Dictionary<string, BuildingDefinition>();
            foreach (string path in paths)
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var definition = root != null ? root.GetComponent<BuildingDefinition>() : null;
                if (definition != null)
                    definitions.Add(path, definition); // A draft or failed .building has no prefab and is skipped.
            }

            // A prefab replaces the .building it was made from: a variant of it (as "Create editable prefab…" saves it),
            // or failing that a prefab with the same file name in the same folder.
            var replaced = new HashSet<string>();
            foreach (string prefab in definitions.Keys.Where(path => path.EndsWith(".prefab")))
            {
                var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(definitions[prefab].gameObject);
                string sourcePath = source != null ? AssetDatabase.GetAssetPath(source) : "";
                replaced.Add(sourcePath.EndsWith(".building") ? sourcePath : Path.ChangeExtension(prefab, ".building").Replace('\\', '/'));
            }

            BuildingDefinition[] buildings = definitions.Where(entry => !replaced.Contains(entry.Key)).Select(entry => entry.Value).ToArray();
            if (set.Buildings != null && set.Buildings.SequenceEqual(buildings))
                return;

            Undo.RecordObject(set, "Refresh building set from folders");
            set.Buildings = buildings;
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssetIfDirty(set);
        }
    }

    // Refreshes every folder-filled building set after a building or prefab in its folders (or the set itself) changes.
    public sealed class BuildingSetFolderWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var changed = imported.Concat(deleted).Concat(moved).Concat(movedFrom)
                .Where(path => path.EndsWith(".building") || path.EndsWith(".prefab") || path.EndsWith(".asset")).ToArray();
            if (changed.Length == 0)
                return;

            // After the import finishes, so the changed prefabs are loadable.
            EditorApplication.delayCall += () =>
            {
                foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(BuildingSet)))
                {
                    string setPath = AssetDatabase.GUIDToAssetPath(guid);
                    var set = AssetDatabase.LoadAssetAtPath<BuildingSet>(setPath);
                    if (set == null || !BuildingSetFolders.HasFolders(set))
                        continue;

                    if (changed.Any(path => path == setPath || set.Folders.Any(folder => !string.IsNullOrEmpty(folder) && path.StartsWith(folder.TrimEnd('/') + "/"))))
                        BuildingSetFolders.Refresh(set);
                }
            };
        }
    }
}
