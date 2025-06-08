using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor.Animations;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public class VRChatAvatarOptimizer : EditorWindow
{
    private GameObject avatarRoot;
    private int selectedTab = 0;
    private string[] tabs = new[] { "Lights", "Shadows", "Materials", "Texture Memory", "Export Report" };

    private List<Light> foundLights = new List<Light>();
    private List<Renderer> foundRenderers = new List<Renderer>();
    private List<Material> foundMaterials = new List<Material>();
    private Dictionary<Material, long> materialSizeMap = new Dictionary<Material, long>();
    private Dictionary<string, int> dropdownSelection = new Dictionary<string, int>();
    private List<TextureOptimizationData> textureOptimizationList = new List<TextureOptimizationData>();

    private Vector2 scrollPos;
    private bool detectInactiveLights = true;
    private bool scanAnimationsForLights = true;
    private bool sortBySize = false;

    private int currentPage = 0;
    private int materialsPerPage = 5;

    public class TextureOptimizationData
    {
        public Texture texture;
        public string assetPath;
        public TextureImporter originalImporter;
        public int originalMaxSize;
        public TextureImporterCompression originalCompression;
        public bool originalCrunch;
        public bool isRecommendedApplied;
        public bool hasAppliedChange;
        public string recommendationLabel;
        public Color recommendationColor;
        public long fileSize;
    }

    private static readonly int[] SupportedSizes = new[] { 32, 64, 128, 256, 512, 1024, 2048 };
    private static readonly TextureImporterCompression[] CompressionModes = new[] {
        TextureImporterCompression.Uncompressed,
        TextureImporterCompression.CompressedLQ,
        TextureImporterCompression.Compressed,
        TextureImporterCompression.CompressedHQ
    };
    private static readonly string[] CompressionModeNames = new[] { "None", "Low", "Normal", "High" };

    [MenuItem("VRChat Tools/Avatar Optimizer")]
    public static void ShowWindow()
    {
        var window = GetWindow<VRChatAvatarOptimizer>("VRChat Avatar Optimizer");
        window.minSize = new Vector2(520, 400);
        window.TryAutoSelect();
    }

    private void TryAutoSelect()
    {
        if (Selection.activeGameObject != null)
        {
            avatarRoot = Selection.activeGameObject;
        }
    }

    private string FormatBytes(long bytes)
    {
        if (bytes > 1024 * 1024) return (bytes / (1024f * 1024f)).ToString("F2") + " MB";
        if (bytes > 1024) return (bytes / 1024f).ToString("F1") + " KB";
        return bytes + " B";
    }

    private void OnGUI()
    {
        avatarRoot = (GameObject)EditorGUILayout.ObjectField("Avatar Root", avatarRoot, typeof(GameObject), true);
        selectedTab = GUILayout.Toolbar(selectedTab, tabs);

        switch (selectedTab)
        {
            case 0: DrawLightsTab(); break;
            case 1: DrawShadowsTab(); break;
            case 2:
                sortBySize = EditorGUILayout.Toggle("Sort by File Size (Largest First)", sortBySize);
                DrawMaterialsTab(); break;
            case 3: DrawSmartTextureMemoryTab(); break;
            case 4: DrawExportTab(); break;
        }
    }

    private bool ValidateAvatar()
    {
        if (avatarRoot == null)
        {
            EditorGUILayout.HelpBox("Please drag an avatar from the Hierarchy.", MessageType.Warning);
            return false;
        }
        return true;
    }

    private void DrawMaterialsTab()
    {
        if (GUILayout.Button("Scan Materials"))
        {
            foundMaterials.Clear();
            materialSizeMap.Clear();
            dropdownSelection.Clear();

            var renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m != null && !foundMaterials.Contains(m))
                        foundMaterials.Add(m);
                }
            }

            foreach (var mat in foundMaterials)
            {
                long totalSize = 0;
                Shader shader = mat.shader;
                int propCount = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < propCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                    {
                        string propName = ShaderUtil.GetPropertyName(shader, i);
                        Texture tex = mat.GetTexture(propName);
                        if (tex != null)
                        {
                            string path = AssetDatabase.GetAssetPath(tex);
                            FileInfo fi = new FileInfo(path);
                            if (fi.Exists) totalSize += fi.Length;
                        }
                    }
                }
                materialSizeMap[mat] = totalSize;
            }

            if (sortBySize)
                foundMaterials = foundMaterials.OrderByDescending(m => materialSizeMap[m]).ToList();
        }

        if (foundMaterials.Count > 0)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Previous") && currentPage > 0) currentPage--;
            GUILayout.Label($"Page {currentPage + 1} / {Mathf.CeilToInt(foundMaterials.Count / (float)materialsPerPage)}");
            if (GUILayout.Button("Next") && (currentPage + 1) * materialsPerPage < foundMaterials.Count) currentPage++;
            EditorGUILayout.EndHorizontal();

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            int start = currentPage * materialsPerPage;
            int end = Mathf.Min(start + materialsPerPage, foundMaterials.Count);
            for (int i = start; i < end; i++)
            {
                var mat = foundMaterials[i];
                EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Material: " + mat.name);
                EditorGUILayout.LabelField("Shader: " + mat.shader.name);
                EditorGUILayout.LabelField("Size: " + FormatBytes(materialSizeMap[mat]));

                Shader shader = mat.shader;
                int propertyCount = ShaderUtil.GetPropertyCount(shader);
                for (int j = 0; j < propertyCount; j++)
                {
                    if (ShaderUtil.GetPropertyType(shader, j) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                    string propName = ShaderUtil.GetPropertyName(shader, j);
                    Texture tex = mat.GetTexture(propName);
                    if (tex == null) continue;

                    string texPath = AssetDatabase.GetAssetPath(tex);
                    TextureImporter importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
                    if (importer == null) continue;

                    FileInfo fi = new FileInfo(texPath);
                    EditorGUILayout.LabelField("- " + propName + ": " + tex.name);
                    EditorGUILayout.LabelField("   File Size: " + FormatBytes(fi.Length));

                    Color color = importer.maxTextureSize > 1024 ? Color.red : importer.maxTextureSize > 512 ? Color.yellow : Color.green;
                    GUI.contentColor = color;
                    EditorGUILayout.LabelField("   Max Size: " + importer.maxTextureSize);
                    GUI.contentColor = Color.white;

                    EditorGUILayout.LabelField("   Compression: " + importer.textureCompression.ToString());
                    EditorGUILayout.LabelField("   Crunch: " + importer.crunchedCompression);
                    if (importer.crunchedCompression)
                        EditorGUILayout.LabelField("   Crunch Quality: " + importer.compressionQuality + "%");

                    string key = tex.name + "_" + propName;
                    int current = 0;
                    List<string> options = new List<string>();
                    for (int s = 0; s < SupportedSizes.Length; s++)
                    {
                        for (int c = 0; c < CompressionModes.Length; c++)
                        {
                            string label = SupportedSizes[s] + " + " + CompressionModeNames[c];
                            options.Add(label);
                            if (SupportedSizes[s] == importer.maxTextureSize && CompressionModes[c] == importer.textureCompression)
                                current = options.Count - 1;
                        }
                    }

                    if (!dropdownSelection.ContainsKey(key)) dropdownSelection[key] = current;
                    dropdownSelection[key] = EditorGUILayout.Popup("Resize & Compress", dropdownSelection[key], options.ToArray());

                    if (GUILayout.Button("Apply Selected Compression"))
                    {
                        int selIndex = dropdownSelection[key];
                        int size = SupportedSizes[selIndex / CompressionModes.Length];
                        TextureImporterCompression comp = CompressionModes[selIndex % CompressionModes.Length];
                        importer.maxTextureSize = size;
                        importer.textureCompression = comp;
                        importer.crunchedCompression = false;
                        importer.SaveAndReimport();
                    }

                    if (GUILayout.Button("Ping Texture"))
                        EditorGUIUtility.PingObject(tex);
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }
        else
        {
            EditorGUILayout.HelpBox("No materials found. Click Scan Materials to begin.", MessageType.Info);
        }
    }

    private void DrawLightsTab()
    {
        detectInactiveLights = EditorGUILayout.Toggle("Include Inactive/Hidden Lights", detectInactiveLights);
        scanAnimationsForLights = EditorGUILayout.Toggle("Scan Animations for Light Keys", scanAnimationsForLights);

        if (GUILayout.Button("Search Lights"))
        {
            if (!ValidateAvatar()) return;

            foundLights.Clear();

            var allTransforms = avatarRoot.GetComponentsInChildren<Transform>(true);
            foreach (var t in allTransforms)
            {
                var lights = t.GetComponents<Light>();
                foreach (var light in lights)
                {
                    foundLights.Add(light);
                }
            }
        }

        if (foundLights.Count > 0)
        {
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            foreach (var light in foundLights)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("GameObject: " + light.gameObject.name);
                EditorGUILayout.LabelField("Type: " + light.type + " | Intensity: " + light.intensity);
                light.enabled = EditorGUILayout.Toggle("Enabled", light.enabled);
                if (GUILayout.Button("Ping")) EditorGUIUtility.PingObject(light.gameObject);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }
        else
        {
            EditorGUILayout.HelpBox("No Light components found. Make sure 'Include Inactive/Hidden Lights' is checked.", MessageType.Warning);
        }
    }

    private void DrawShadowsTab()
    {
        if (GUILayout.Button("Search Renderers"))
        {
            if (!ValidateAvatar()) return;
            foundRenderers.Clear();
            foundRenderers.AddRange(avatarRoot.GetComponentsInChildren<Renderer>(true));
        }

        if (foundRenderers.Count > 0)
        {
            if (GUILayout.Button("Disable All Shadows"))
            {
                foreach (var r in foundRenderers)
                {
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    r.receiveShadows = false;
                }
            }

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            foreach (var r in foundRenderers)
            {
                if (r == null) continue;
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("GameObject: " + r.gameObject.name);
                r.shadowCastingMode = (ShadowCastingMode)EditorGUILayout.EnumPopup("Cast Shadows", r.shadowCastingMode);
                r.receiveShadows = EditorGUILayout.Toggle("Receive Shadows", r.receiveShadows);
                if (GUILayout.Button("Ping")) EditorGUIUtility.PingObject(r.gameObject);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }
        else
        {
            EditorGUILayout.HelpBox("No renderers found. Click 'Search Renderers' first.", MessageType.Info);
        }
    }

    private void DrawExportTab()
    {
        if (GUILayout.Button("Generate Optimization Report"))
        {
            string report = "# VRChat Avatar Optimization Report\n\n";
            report += $"**Avatar:** {avatarRoot?.name ?? "None"}\n\n";
            report += $"**Lights:** {foundLights.Count}\n";
            report += $"**Renderers:** {foundRenderers.Count}\n";
            report += $"**Unique Materials:** {foundMaterials.Count}\n\n";

            report += "## Materials List:\n";
            foreach (var mat in foundMaterials)
            {
                report += $"- {mat.name} (Shader: {mat.shader.name})\n";
            }

            string path = EditorUtility.SaveFilePanel("Save Report", "", "AvatarReport.md", "md");
            if (!string.IsNullOrEmpty(path))
            {
                File.WriteAllText(path, report);
                Debug.Log("✅ Report saved to: " + path);
            }
        }
    }


    private void ScanTextureMemorySmart()
    {
        textureOptimizationList.Clear();
        var renderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
        HashSet<Texture> seenTextures = new HashSet<Texture>();

        foreach (var r in renderers)
        {
            foreach (var mat in r.sharedMaterials)
            {
                if (mat == null || mat.shader == null) continue;
                int propCount = ShaderUtil.GetPropertyCount(mat.shader);
                for (int i = 0; i < propCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                        continue;

                    var tex = mat.GetTexture(ShaderUtil.GetPropertyName(mat.shader, i));
                    if (tex == null || seenTextures.Contains(tex)) continue;

                    seenTextures.Add(tex);

                    string path = AssetDatabase.GetAssetPath(tex);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null) continue;

                    FileInfo fi = new FileInfo(path);
                    long size = fi.Exists ? fi.Length : 0;

                    var data = new TextureOptimizationData
                    {
                        texture = tex,
                        assetPath = path,
                        originalImporter = importer,
                        originalMaxSize = importer.maxTextureSize,
                        originalCompression = importer.textureCompression,
                        originalCrunch = importer.crunchedCompression,
                        fileSize = size,
                        isRecommendedApplied = false
                    };

                    if (size > 15 * 1024 * 1024)
                    {
                        data.recommendationLabel = "Reduce to 512 + Compressed";
                        data.recommendationColor = Color.red;
                    }
                    else if (size > 8 * 1024 * 1024)
                    {
                        data.recommendationLabel = "Reduce to 1024 + Compressed";
                        data.recommendationColor = Color.yellow;
                    }
                    else
                    {
                        data.recommendationLabel = "Looks O.K";
                        data.recommendationColor = Color.green;
                        data.isRecommendedApplied = false;
                        data.hasAppliedChange = false;
                    }

                    textureOptimizationList.Add(data);
                }
            }
        }
    }

    private void DrawSmartTextureMemoryTab()
    {
        if (GUILayout.Button("Smart Scan Textures"))
        {
            ScanTextureMemorySmart();
        }

        if (textureOptimizationList.Count > 0)
        {
            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
            foreach (var texData in textureOptimizationList)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Texture: " + texData.texture.name);
                EditorGUILayout.LabelField("Size: " + FormatBytes(texData.fileSize));

                GUI.contentColor = texData.recommendationColor;
                EditorGUILayout.LabelField("Impact: " + texData.recommendationLabel);
                GUI.contentColor = Color.white;

                var importer = AssetImporter.GetAtPath(texData.assetPath) as TextureImporter;
                if (importer == null) continue;

                EditorGUILayout.LabelField("Current Max Size: " + importer.maxTextureSize);
                EditorGUILayout.LabelField("Compression: " + importer.textureCompression);
                EditorGUILayout.LabelField("Crunch: " + importer.crunchedCompression);

                // Show "Apply Recommendation" if not already applied and recommendation exists
                if (!texData.hasAppliedChange && texData.recommendationColor != Color.green)
                {
                    if (GUILayout.Button("Apply Recommendation"))
                    {
                        texData.originalMaxSize = importer.maxTextureSize;
                        texData.originalCompression = importer.textureCompression;
                        texData.originalCrunch = importer.crunchedCompression;

                        importer.maxTextureSize = texData.recommendationLabel.Contains("512") ? 512 : 1024;
                        importer.textureCompression = TextureImporterCompression.Compressed;
                        importer.crunchedCompression = false;
                        importer.SaveAndReimport();

                        texData.hasAppliedChange = true;
                        texData.isRecommendedApplied = true;
                        texData.recommendationLabel = "Looks O.K";
                        texData.recommendationColor = Color.green;
                    }
                }

                // Show Undo only if recommendation was applied
                if (texData.hasAppliedChange)
                {
                    if (GUILayout.Button("Undo"))
                    {
                        importer.maxTextureSize = texData.originalMaxSize;
                        importer.textureCompression = texData.originalCompression;
                        importer.crunchedCompression = texData.originalCrunch;
                        importer.SaveAndReimport();

                        texData.hasAppliedChange = false;
                        texData.isRecommendedApplied = false;

                        // Recalculate original recommendation
                        if (texData.fileSize > 15 * 1024 * 1024)
                        {
                            texData.recommendationLabel = "Reduce to 512 + Compressed";
                            texData.recommendationColor = Color.red;
                        }
                        else if (texData.fileSize > 8 * 1024 * 1024)
                        {
                            texData.recommendationLabel = "Reduce to 1024 + Compressed";
                            texData.recommendationColor = Color.yellow;
                        }
                        else
                        {
                            texData.recommendationLabel = "Looks O.K";
                            texData.recommendationColor = Color.green;
                        }
                    }
                }

                if (GUILayout.Button("Ping Texture"))
                    EditorGUIUtility.PingObject(texData.texture);

                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }
        else
        {
            EditorGUILayout.HelpBox("No textures scanned yet.", MessageType.Info);
        }
    }
}
