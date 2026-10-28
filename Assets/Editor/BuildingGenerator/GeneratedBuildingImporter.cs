using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace SimpleFPS.EditorTools
{
    // A self-contained export: meshes, colors, collision geometry, and placement metadata.
    // No generated code or external paths are evaluated during import.
    [ScriptedImporter(3, "building")]
    public sealed class GeneratedBuildingImporter : ScriptedImporter
    {
        [SerializeField, Min(1)] private int weight = 1;
        [SerializeField, Min(0)] private int repeatCooldownDistance = 10;

        [Serializable]
        private sealed class BuildingData
        {
            public int version;
            public bool draft; // The generator also embeds its editable plan as "source"; JsonUtility skips it.
            public string name, kind, description;
            public int width, depth;
            public float tileSize, agentRadius, agentHeight;
            public string north, east, south, west;
            public PaletteData[] materials;
            public MeshData[] meshes;
            public PointData[] points;
        }

        [Serializable]
        private sealed class PaletteData
        {
            public string id, color;
        }

        [Serializable]
        private sealed class MeshData
        {
            public string name, material, role;
            public int level;
            public float[] vertices, uv;
            public int[] triangles;
        }

        [Serializable]
        private sealed class PointData
        {
            public string kind, name;
            public float x, y, z, yaw;
            public int level;
        }

        public override void OnImportAsset(AssetImportContext context)
        {
            GameObject root = null;
            var owned = new List<UnityEngine.Object>();
            try
            {
                if (new FileInfo(context.assetPath).Length > 50_000_000)
                    throw new InvalidDataException("Building export exceeds 50 MB.");
                var data = JsonUtility.FromJson<BuildingData>(File.ReadAllText(context.assetPath));
                Validate(data);
                // Import workers do not have the editor's Shader.Find cache on a fresh project.
                const string shaderPath = "Packages/com.unity.render-pipelines.universal/Shaders/Lit.shader";
                context.DependsOnSourceAsset(shaderPath);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
                if (shader == null) throw new InvalidDataException("The URP Lit shader is unavailable.");
                root = new GameObject(data.name);
                var definition = root.AddComponent<BuildingDefinition>();
                definition.Category = data.kind == "SimpleBlocking" ? BuildingCategory.SimpleBlocking : BuildingCategory.Complex;
                definition.FootprintSize = new Vector2Int(data.width, data.depth);
                definition.North = Side(data.north); definition.East = Side(data.east);
                definition.South = Side(data.south); definition.West = Side(data.west);
                definition.Weight = Mathf.Max(1, weight);
                definition.RepeatCooldownDistance = Mathf.Max(0, repeatCooldownDistance);

                var palette = new Dictionary<string, Material>();
                foreach (var source in data.materials)
                {
                    ColorUtility.TryParseHtmlString(source.color, out var color);
                    var material = new Material(shader) { name = source.id };
                    owned.Add(material);
                    material.SetColor("_BaseColor", color);
                    material.SetFloat("_Smoothness", .18f);
                    palette.Add(source.id, material);
                    context.AddObjectToAsset("material_" + source.id, material);
                }
                foreach (var source in data.meshes)
                {
                    var mesh = new Mesh { name = source.name };
                    owned.Add(mesh);
                    int count = source.vertices.Length / 3;
                    if (count > 65535) mesh.indexFormat = IndexFormat.UInt32;
                    var vertices = new Vector3[count];
                    var uv = new Vector2[count];
                    for (int i = 0; i < count; i++)
                    {
                        vertices[i] = new Vector3(source.vertices[i * 3], source.vertices[i * 3 + 1], source.vertices[i * 3 + 2]);
                        uv[i] = new Vector2(source.uv[i * 2], source.uv[i * 2 + 1]);
                    }
                    mesh.vertices = vertices; mesh.uv = uv; mesh.triangles = source.triangles;
                    mesh.RecalculateNormals(); mesh.RecalculateBounds(); mesh.RecalculateTangents();
                    context.AddObjectToAsset("mesh_" + source.name, mesh);
                    var child = new GameObject(source.name);
                    child.transform.SetParent(root.transform, false);
                    child.layer = 0; // The game's NavMeshSurface includes Default-layer physics colliders.
                    child.AddComponent<MeshFilter>().sharedMesh = mesh;
                    child.AddComponent<MeshRenderer>().sharedMaterial = palette[source.material];
                    child.AddComponent<MeshCollider>().sharedMesh = mesh; // Non-convex: preserves rooms and openings.
                }
                if (data.points != null && data.points.Length > 0)
                {
                    var pointsRoot = new GameObject("Generated Points");
                    pointsRoot.transform.SetParent(root.transform, false);
                    for (int i = 0; i < data.points.Length; i++)
                    {
                        var point = data.points[i];
                        var child = new GameObject($"{point.kind} {i + 1:000} - {point.name}");
                        child.transform.SetParent(pointsRoot.transform, false);
                        child.transform.localPosition = new Vector3(point.x, point.y, point.z);
                        child.transform.localRotation = Quaternion.Euler(0, point.yaw, 0);
                        if (point.kind == "Patrol") child.AddComponent<PatrolWaypoint>();
                        else child.AddComponent<PickupSpawnPoint>().Category = point.kind == "Weapon"
                            ? PickupSpawnPoint.PickupSpawnCategory.Weapon : PickupSpawnPoint.PickupSpawnCategory.Health;
                    }
                }
                var agent = UnityEngine.AI.NavMesh.GetSettingsByID(0);
                if (Mathf.Abs(agent.agentRadius - data.agentRadius) > .01f || Mathf.Abs(agent.agentHeight - data.agentHeight) > .01f)
                    context.LogImportWarning("Navigation dimensions differ from the generator profile. Update game-profile.json and recheck this design.");
                context.AddObjectToAsset("building", root);
                context.SetMainObject(root);
            }
            catch (Exception exception)
            {
                if (root != null) DestroyImmediate(root);
                foreach (var asset in owned) if (asset != null) DestroyImmediate(asset);
                context.LogImportError("Cannot import building: " + exception.Message);
            }
        }

        private static BuildingSideRequirement Side(string value)
        {
            switch (value)
            {
                case "Any": return BuildingSideRequirement.Any;
                case "Road": return BuildingSideRequirement.RequiresRoad;
                case "NoRoad": return BuildingSideRequirement.RequiresNoRoad;
                case "RequiresRoad": return BuildingSideRequirement.RequiresRoad;
                case "RequiresNoRoad": return BuildingSideRequirement.RequiresNoRoad;
                case "RequiresLedgeDown": return BuildingSideRequirement.RequiresLedgeDown;
                case "RequiresLedgeUp": return BuildingSideRequirement.RequiresLedgeUp;
                case "RequiresComplexBuilding": return BuildingSideRequirement.RequiresComplexBuilding;
                case "RequiresNoComplexBuilding": return BuildingSideRequirement.RequiresNoComplexBuilding;
                case "RequiresBlockingBuilding": return BuildingSideRequirement.RequiresBlockingBuilding;
                case "RequiresNoBlockingBuilding": return BuildingSideRequirement.RequiresNoBlockingBuilding;
                default: throw new InvalidDataException("Unknown side requirement: " + value);
            }
        }

        private static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidDataException(message);
        }

        private static bool Finite(float number) => !float.IsNaN(number) && !float.IsInfinity(number);

        private static void Validate(BuildingData data)
        {
            Require(data != null && (data.version == 1 || data.version == 2), "Unsupported or empty building format.");
            Require(!data.draft, "This is an unfinished draft. Open it in the building generator, fix the failing checks and save again.");
            Require(!string.IsNullOrWhiteSpace(data.name) && data.name.Length <= 100, "Invalid building name.");
            Require(data.kind == "Complex" || data.kind == "SimpleBlocking", "Unknown building kind.");
            Require(data.width >= 1 && data.width <= 20 && data.depth >= 1 && data.depth <= 20, "Footprint must be 1–20 tiles per side.");
            Require(Finite(data.tileSize) && Mathf.Abs(data.tileSize - 15) < .001f, "This project uses 15-unit tiles.");
            Require(Finite(data.agentRadius) && data.agentRadius > 0 && Finite(data.agentHeight) && data.agentHeight > 0, "Invalid agent dimensions.");
            Side(data.north); Side(data.east); Side(data.south); Side(data.west);
            Require(data.version < 2 || data.points != null, "Export is missing its point list.");
            if (data.points != null)
            {
                Require(data.points.Length <= 600 && (data.kind != "SimpleBlocking" || data.points.Length == 0), "Invalid authoring point count.");
                foreach (var point in data.points)
                    Require(point != null && (point.kind == "Weapon" || point.kind == "Health" || point.kind == "Patrol") &&
                        !string.IsNullOrWhiteSpace(point.name) && point.name.Length <= 100 && point.level >= 0 && point.level <= 15 &&
                        Finite(point.x) && Finite(point.y) && Finite(point.z) && Finite(point.yaw) && point.y >= 0 && point.y <= 60 &&
                        Mathf.Abs(point.x) <= data.width * data.tileSize / 2 && Mathf.Abs(point.z) <= data.depth * data.tileSize / 2,
                        "Invalid weapon/health/patrol point.");
            }
            Require(data.materials != null && data.materials.Length > 0 && data.materials.Length <= 28, "Invalid material palette.");
            var materialIds = new HashSet<string>();
            foreach (var material in data.materials)
            {
                Require(material != null && !string.IsNullOrWhiteSpace(material.id) && material.id.Length <= 32 && materialIds.Add(material.id), "Invalid or duplicate material ID.");
                Require(ColorUtility.TryParseHtmlString(material.color, out var color) && Mathf.Approximately(color.a, 1), "Invalid opaque material color.");
            }
            Require(data.meshes != null && data.meshes.Length > 0 && data.meshes.Length <= 1000, "Invalid mesh list.");
            var meshNames = new HashSet<string>();
            int vertexTotal = 0;
            foreach (var mesh in data.meshes)
            {
                Require(mesh != null && !string.IsNullOrWhiteSpace(mesh.name) && mesh.name.Length <= 120 && meshNames.Add(mesh.name), "Invalid or duplicate mesh name.");
                Require(materialIds.Contains(mesh.material), "Mesh references a missing material.");
                Require(mesh.level >= 0 && mesh.level <= 15, "Invalid floor level.");
                Require(mesh.vertices != null && mesh.vertices.Length >= 9 && mesh.vertices.Length % 3 == 0, "Invalid vertices.");
                int count = mesh.vertices.Length / 3;
                vertexTotal += count;
                Require(vertexTotal <= 1_000_000, "Too many vertices.");
                Require(mesh.uv != null && mesh.uv.Length == count * 2 && mesh.uv.All(Finite), "Invalid UV coordinates.");
                Require(mesh.triangles != null && mesh.triangles.Length > 0 && mesh.triangles.Length <= 3_000_000 && mesh.triangles.Length % 3 == 0 &&
                    mesh.triangles.All(index => index >= 0 && index < count), "Invalid triangle indices.");
                for (int i = 0; i < mesh.vertices.Length; i += 3)
                    Require(Finite(mesh.vertices[i]) && Finite(mesh.vertices[i + 1]) && Finite(mesh.vertices[i + 2]) &&
                        Mathf.Abs(mesh.vertices[i]) <= data.width * data.tileSize / 2 + .001f &&
                        Mathf.Abs(mesh.vertices[i + 2]) <= data.depth * data.tileSize / 2 + .001f &&
                        mesh.vertices[i + 1] >= -.51f && mesh.vertices[i + 1] <= 60.01f, "Geometry is non-finite or outside the lot/height limits.");
            }
        }
    }

    [CustomEditor(typeof(GeneratedBuildingImporter))]
    public sealed class GeneratedBuildingImporterEditor : ScriptedImporterEditor
    {
        private BuildingSet buildingSet;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("weight"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("repeatCooldownDistance"));
            ApplyRevertGUI();
            var importer = (GeneratedBuildingImporter)target;
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(importer.assetPath);
            if (root == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("This is a generated prefab asset. Drag it into a scene, or create an editable prefab to add spawn markers. World generation needs it in a BuildingSet. The scene's NavMesh is baked after placement.", MessageType.Info);
            if (GUILayout.Button("Create editable prefab…"))
            {
                string path = EditorUtility.SaveFilePanelInProject("Create editable building prefab", root.name, "prefab", "Choose where to save the editable prefab.");
                if (!string.IsNullOrEmpty(path))
                {
                    var previewScene = EditorSceneManager.NewPreviewScene();
                    try
                    {
                        var instance = (GameObject)PrefabUtility.InstantiatePrefab(root, previewScene);
                        var prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
                        Selection.activeObject = prefab;
                        EditorGUIUtility.PingObject(prefab);
                    }
                    finally { EditorSceneManager.ClosePreviewScene(previewScene); }
                }
            }
            buildingSet = (BuildingSet)EditorGUILayout.ObjectField("Building set", buildingSet, typeof(BuildingSet), false);
            using (new EditorGUI.DisabledScope(buildingSet == null))
            {
                if (GUILayout.Button("Add generated building to set"))
                {
                    var definition = root.GetComponent<BuildingDefinition>();
                    var definitions = buildingSet.Buildings ?? Array.Empty<BuildingDefinition>();
                    if (!definitions.Contains(definition))
                    {
                        Undo.RecordObject(buildingSet, "Add generated building");
                        buildingSet.Buildings = definitions.Concat(new[] { definition }).ToArray();
                        EditorUtility.SetDirty(buildingSet);
                        AssetDatabase.SaveAssetIfDirty(buildingSet);
                    }
                }
            }
        }
    }
}
