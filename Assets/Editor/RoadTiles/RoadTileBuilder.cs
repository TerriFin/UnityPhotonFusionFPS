using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimpleFPS.EditorTools.Roads;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SimpleFPS.EditorTools
{
    // Builds the road tile prefabs from RoadTileDesigns into Roads/Generated and puts them in the RoadTiles set.
    // Runs by itself after the designs change (RoadTileDesigns.Version differs from the last build), or from
    // SimpleFPS > World Generation > Rebuild Road Tiles.
    //
    // Each tile is one prefab: a RoadTileDefinition, a "Ground" mesh with a MeshCollider (asphalt, curbs, sidewalks),
    // a render-only "Props" mesh (cars, street furniture, markings, litter) and one BoxCollider per solid prop under
    // "Colliders", all on the Default layer so the NavMeshSurface bakes around them, and a SpawnPoint per team spawn
    // on boundary exits. Both meshes use one material
    // whose base map is a palette strip (one texel per RoadPalette colour): two draw calls a tile.
    [InitializeOnLoad]
    public static class RoadTileBuilder
    {
        const string Folder = "Assets/Prefabs/WorldGeneration/Roads/Generated";
        const string TileSetPath = "Assets/Prefabs/WorldGeneration/RoadTiles.asset";
        const string VersionPath = Folder + "/BuildVersion.txt";

        static RoadTileBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                string built = File.Exists(VersionPath) ? File.ReadAllText(VersionPath).Trim() : "";
                if (built != RoadTileDesigns.Version.ToString()) Build();
            };
        }

        [MenuItem("SimpleFPS/World Generation/Rebuild Road Tiles")]
        public static void Build()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder(Path.GetDirectoryName(Folder).Replace('\\', '/'), Path.GetFileName(Folder));
            var ids = RoadPalette.Colors.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var material = PaletteMaterial(ids);
            var built = new List<RoadTileDefinition>();
            foreach (var model in RoadTileDesigns.All())
            {
                foreach (string problem in model.Problems.Distinct())
                    Debug.LogWarning($"Road tile {model.Name}: {problem}");
                built.Add(BuildTile(model, ids, material));
            }
            UpdateTileSet(built);
            File.WriteAllText(VersionPath, RoadTileDesigns.Version.ToString());
            AssetDatabase.ImportAsset(VersionPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"Built {built.Count} road tiles (design version {RoadTileDesigns.Version}) into {Folder}.");
        }

        static Material PaletteMaterial(List<string> ids)
        {
            string texturePath = Folder + "/RoadPalette.asset", materialPath = Folder + "/RoadPalette.mat";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null)
            {
                texture = new Texture2D(ids.Count, 1, TextureFormat.RGBA32, false);
                AssetDatabase.CreateAsset(texture, texturePath);
            }
            else texture.Reinitialize(ids.Count, 1, TextureFormat.RGBA32, false);
            texture.name = "RoadPalette";
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            for (int i = 0; i < ids.Count; i++)
            {
                ColorUtility.TryParseHtmlString(RoadPalette.Colors[ids[i]], out var color);
                texture.SetPixel(i, 0, color);
            }
            texture.Apply(false);
            EditorUtility.SetDirty(texture);

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "RoadPalette" };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Metallic", 0);
            material.SetFloat("_Smoothness", RoadPalette.Smoothness);
            material.SetFloat("_SmoothnessTextureChannel", 0); // the material's value, not the texture's alpha
            material.DisableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
            EditorUtility.SetDirty(material);
            return material;
        }

        static Vector3 V(Vec v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);

        static Mesh MeshFrom(SortedDictionary<string, MeshBuffer> buffers, List<string> ids, string name)
        {
            var vertices = new List<Vector3>();
            var uv = new List<Vector2>();
            var triangles = new List<int>();
            foreach (var pair in buffers)
            {
                int first = vertices.Count;
                var texel = new Vector2((ids.IndexOf(pair.Key) + .5f) / ids.Count, .5f);
                foreach (var v in pair.Value.Vertices) { vertices.Add(V(v)); uv.Add(texel); }
                foreach (int t in pair.Value.Triangles) triangles.Add(first + t);
            }
            var mesh = new Mesh { name = name };
            if (vertices.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals(); // faces share no vertices: flat shading
            mesh.RecalculateBounds();
            return mesh;
        }

        static RoadTileDefinition BuildTile(TileModel model, List<string> ids, Material material)
        {
            string meshPath = $"{Folder}/{model.Name} Meshes.asset", prefabPath = $"{Folder}/{model.Name}.prefab";
            var ground = MeshFrom(model.Ground, ids, model.Name + " Ground");
            var props = MeshFrom(model.Props, ids, model.Name + " Props");
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(ground, meshPath);
            AssetDatabase.AddObjectToAsset(props, meshPath);

            var root = new GameObject(model.Name);
            var definition = root.AddComponent<RoadTileDefinition>();
            RoadSocket Socket(bool open) => open ? RoadSocket.Road : RoadSocket.Closed;
            definition.North = model.Boundary ? RoadSocket.Exit : Socket(model.North); definition.East = Socket(model.East);
            definition.South = Socket(model.South); definition.West = Socket(model.West);
            definition.IsBoundaryTile = model.Boundary; definition.IsHeightChangeRamp = model.Ramp;
            definition.Weight = model.Weight; definition.RepeatCooldown = model.RepeatCooldown;

            GameObject Part(string name, Mesh mesh)
            {
                var part = new GameObject(name);
                part.transform.SetParent(root.transform, false);
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                part.AddComponent<MeshRenderer>().sharedMaterial = material;
                return part;
            }
            Part("Ground", ground).AddComponent<MeshCollider>().sharedMesh = ground;
            Part("Props", props);
            var colliders = new GameObject("Colliders");
            colliders.transform.SetParent(root.transform, false);
            foreach (var c in model.Colliders)
            {
                var box = new GameObject(c.Name);
                box.transform.SetParent(colliders.transform, false);
                box.transform.localPosition = V(c.Frame.Point(c.Center));
                box.transform.localRotation = Quaternion.LookRotation(V(c.Frame.Forward), V(c.Frame.Up));
                box.AddComponent<BoxCollider>().size = V(c.Size);
            }
            foreach (var f in model.SpawnPoints)
            {
                var spawn = new GameObject("SpawnPoint");
                spawn.transform.SetParent(root.transform, false);
                spawn.transform.localPosition = V(f.Origin);
                spawn.transform.localRotation = Quaternion.LookRotation(V(f.Forward), V(f.Up));
                spawn.AddComponent<SpawnPoint>();
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            return AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<RoadTileDefinition>();
        }

        // The road shape a definition covers in any rotation, e.g. "Road,Closed,Road,Closed" for a straight.
        static string Shape(RoadTileDefinition d)
        {
            var sockets = new[] { d.North, d.East, d.South, d.West };
            string best = null;
            for (int r = 0; r < 4; r++)
            {
                string s = string.Join(",", Enumerable.Range(0, 4).Select(i => sockets[(i + r) % 4].ToString()));
                if (best == null || string.CompareOrdinal(s, best) < 0) best = s;
            }
            return $"{best} {d.Environment} boundary:{d.IsBoundaryTile} ramp:{d.IsHeightChangeRamp}";
        }

        // Generated tiles replace the hand-made tiles of the same shape in the set (the old prefabs stay on disk);
        // shapes with no generated tile yet keep their hand-made tiles.
        static void UpdateTileSet(List<RoadTileDefinition> built)
        {
            var set = AssetDatabase.LoadAssetAtPath<RoadTileSet>(TileSetPath);
            if (set == null) { Debug.LogWarning("Road tile set not found at " + TileSetPath); return; }
            var shapes = new HashSet<string>(built.Select(Shape));
            bool Generated(RoadTileDefinition d) => AssetDatabase.GetAssetPath(d).StartsWith(Folder + "/");
            var current = (set.Tiles ?? new RoadTileDefinition[0]).Where(t => t != null).ToList();
            var replaced = current.Where(t => !Generated(t) && shapes.Contains(Shape(t))).ToList();
            var tiles = current.Where(t => !Generated(t) && !shapes.Contains(Shape(t))).Concat(built).ToArray();
            if (set.Tiles != null && set.Tiles.SequenceEqual(tiles)) return;
            Undo.RecordObject(set, "Update road tiles");
            set.Tiles = tiles;
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssetIfDirty(set);
            if (replaced.Count > 0)
                Debug.Log("Road tile set: generated tiles replace " + string.Join(", ", replaced.Select(t => t.name)) + " (prefabs kept on disk).");
        }
    }
}
