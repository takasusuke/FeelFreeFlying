using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FeelFreeFlying.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// 街をタイルに割って変換する（docs/m2-plan.md §2）。**1タイル = 3次メッシュ1枚。**
    ///
    /// 全部を1つのシーンに置く方式は3km四方で1% lowが60fpsを割り、
    /// 取り込み中のEditorも12.3GBまで膨らむ（→ `m0-plan.md` §5.1）。
    /// 都市を増やすには「近くだけ読む」しかなく、そのためにまず**タイル単位のシーン**を作る。
    ///
    ///   Unity.exe -projectPath . -batchmode -logFile &lt;ログ&gt; `
    ///     -executeMethod FeelFreeFlying.EditorTools.M2TilePipeline.BuildTiles
    /// </summary>
    public static class M2TilePipeline
    {
        public const string TileDir = "Assets/Scenes/Tiles";

        /// <summary>位置表の置き場所。**実行時に読むのでResourcesに置く。**</summary>
        private const string CatalogDir = "Assets/Resources";
        private const string CatalogPath = CatalogDir + "/" + TileCatalog.ResourcePath + ".json";

        /// <summary>新宿駅周辺。**M0の計測と同じ範囲**なので、数字を並べて比べられる。</summary>
        private static readonly string[] DefaultGridCodes =
        {
            "53394525", "53394526",
            "53394535", "53394536",
        };

        /// <summary>
        /// 取り込む範囲。**パイプラインの入力はメッシュコードの集合**（→ m2-plan.md §2）なので、
        /// コマンドラインから渡せるようにする。
        ///
        ///   -ffm2tiles-grid 53394525,53394526,53394527,...
        /// </summary>
        private static string[] GridCodes => ResolveGridCodes();

        /// <summary>他の工程（属性の書き出し等）が同じ範囲を見るための入口。</summary>
        public static string[] GridCodesInUse => ResolveGridCodes();

        private const string GridArg = "-ffm2tiles-grid";

        private static string[] ResolveGridCodes()
        {
            string[] args = Environment.GetCommandLineArgs();

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != GridArg) continue;

                string[] codes = args[i + 1]
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(code => code.Trim())
                    .Where(code => code.Length > 0)
                    .ToArray();

                if (codes.Length > 0) return codes;
            }

            return DefaultGridCodes;
        }

        public static string TileScenePath(string gridCode) => $"{TileDir}/Tile_{gridCode}.unity";

        [MenuItem("Tools/FeelFreeFlying/M2: 街をタイルに割って取り込む")]
        public static void BuildTilesFromMenu() => _ = BuildTilesAsync(exitWhenDone: false);

        public static void BuildTiles() => _ = BuildTilesAsync(exitWhenDone: true);

        /// <summary>
        /// 取り込み済みのタイルに当たり判定と位置表だけを足す。
        /// **取り込みは1枚1〜2分かかる**ので、そこを変えていない時にやり直さないための入口。
        /// </summary>
        [MenuItem("Tools/FeelFreeFlying/M2: タイルに当たり判定と位置表を足す")]
        public static void PrepareTiles()
        {
            try
            {
                PrepareTilesInProcess();
                EditorApplication.Exit(M2Verify.Check() ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M2Tile] 失敗: {exception}");
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// 取り込みの後始末（外壁・当たり判定・位置表・焼き込み・属性表）。
        /// **タイルを差し替える工程からも呼ぶ**ので、ここでは終了しない。
        /// </summary>
        /// <param name="paintWalls">
        /// 外壁を高さ別に塗り直すか。**ランドマークの実写テクスチャを当てた直後はfalse**——
        /// 塗り直すと、せっかく残した実写を無地の外壁で上書きしてしまう。
        /// </param>
        public static void PrepareTilesInProcess(bool paintWalls = true)
        {
            WriteCatalog(GridCodes.Where(code => File.Exists(TileScenePath(code))), paintWalls);
            M2TileOcclusion.BakeAll();

            // **属性表も同じ範囲で出し直す。** タイルを広げた時に4枚ぶんの表が残り、
            // 26,559棟中17,765棟が表に無い状態になった（→ m2-plan.md §4.5）
            M2AttributeExport.ExportAll();
        }

        private static async Task BuildTilesAsync(bool exitWhenDone)
        {
            try
            {
                Directory.CreateDirectory(TileDir);
                var counts = new Dictionary<string, int>();

                foreach (string gridCode in GridCodes)
                {
                    string scenePath = TileScenePath(gridCode);
                    Debug.Log($"[M2Tile] {gridCode} を取り込みます → {scenePath}");

                    int buildings = await M0CityImport.ImportTile(gridCode, scenePath, GridCodes);
                    counts[gridCode] = buildings;
                }

                RegisterTilesInBuildSettings(GridCodes.Select(TileScenePath));
                WriteCatalog(GridCodes);

                foreach (KeyValuePair<string, int> pair in counts)
                {
                    long bytes = new FileInfo(TileScenePath(pair.Key)).Length;
                    Debug.Log($"[M2Tile] {pair.Key}: 建物 {pair.Value} 棟 / {bytes / 1024f / 1024f:F1} MB");
                }

                Debug.Log($"[M2Tile] 完了: {counts.Count} タイル / 合計 {counts.Values.Sum()} 棟");

                // **オクルージョンカリングまでを1コマンドに含める。** 別工程にすると
                // 焼き忘れたタイルが混ざり、どのタイルがどの条件か分からなくなる
                M2TileOcclusion.BakeAll();

                // **属性表も同じ範囲で出し直す。** タイルを広げた時に4枚ぶんの表が残り、
                // 26,559棟中17,765棟が表に無い状態になった（→ m2-plan.md §4.5）
                M2AttributeExport.ExportAll();

                // **作った直後に確かめる。** ここを人の目に任せると、
                // 4枚が原点に重なっていても気づかない（→ m2-plan.md §4.2）
                bool ok = M2Verify.Check();

                if (exitWhenDone) EditorApplication.Exit(ok ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M2Tile] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// タイルの位置表を書き出す（実行時に読む → <see cref="FeelFreeFlying.Flight.TileCatalog"/>）。
        /// **メッシュコードから座標を計算せず、取り込んだ結果を測って書く。**
        /// ついでに当たり判定も焼き込む——屋上を歩けることはM1で確かめた仕様なので、
        /// タイルに割った途端に歩けなくなるのは退行になる。
        /// </summary>
        private static void WriteCatalog(IEnumerable<string> gridCodes, bool paintWalls = true)
        {
            var entries = new List<TileCatalog.TileEntry>();
            var materialCache = new Dictionary<string, Material>();
            var tierTotals = new Dictionary<string, int>();

            foreach (string gridCode in gridCodes)
            {
                string scenePath = TileScenePath(gridCode);
                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

                // **1回開く間に全部済ませる。** 200MBのシーンを開き直すのは高い
                int painted = paintWalls ? M2TileMaterials.Paint(materialCache, tierTotals) : 0;
                int colliders = AddColliders();
                Bounds bounds = MeasureBounds();

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, scenePath);

                string farPath = M2FarTiles.FarScenePath(gridCode);

                entries.Add(new TileCatalog.TileEntry
                {
                    sceneName = Path.GetFileNameWithoutExtension(scenePath),
                    farSceneName = File.Exists(farPath) ? Path.GetFileNameWithoutExtension(farPath) : string.Empty,
                    gridCode = gridCode,
                    center = bounds.center,
                    size = bounds.size,
                });

                Debug.Log($"[M2Tile] {gridCode}: 外壁 {painted} 棟 / 当たり判定 {colliders} 件 / " +
                          $"範囲 {bounds.size.x:F0} x {bounds.size.z:F0} m / 中心 {bounds.center}");
            }

            foreach (KeyValuePair<string, int> tier in tierTotals.OrderByDescending(pair => pair.Value))
            {
                Debug.Log($"[M2Tile]   {tier.Key}: {tier.Value} 棟");
            }

            var catalog = new TileCatalog();
            catalog.Replace(entries);

            Directory.CreateDirectory(CatalogDir);
            File.WriteAllText(CatalogPath, JsonUtility.ToJson(catalog, true), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(CatalogPath);

            Debug.Log($"[M2Tile] 位置表: {CatalogPath} / {entries.Count} 件");
        }

        /// <summary>
        /// 既にある位置表に、遠景タイルの名前だけを足す。
        /// **遠景を作るたびに近景を開き直すのは高い**（1枚200MB超）ので、表だけ書き換える。
        /// </summary>
        public static void WriteFarNamesIntoCatalog(IEnumerable<string> gridCodes)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(CatalogPath);
            if (asset == null)
            {
                Debug.LogWarning($"[M2Tile] 位置表がありません: {CatalogPath}。遠景の名前は書けません。");
                return;
            }

            TileCatalog catalog = JsonUtility.FromJson<TileCatalog>(asset.text);
            if (catalog == null || catalog.Tiles.Count == 0) return;

            var wanted = new HashSet<string>(gridCodes);
            int updated = 0;

            foreach (TileCatalog.TileEntry entry in catalog.Tiles)
            {
                if (!wanted.Contains(entry.gridCode)) continue;

                string farPath = M2FarTiles.FarScenePath(entry.gridCode);
                if (!File.Exists(farPath)) continue;

                entry.farSceneName = Path.GetFileNameWithoutExtension(farPath);
                updated++;
            }

            File.WriteAllText(CatalogPath, JsonUtility.ToJson(catalog, true), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(CatalogPath);

            Debug.Log($"[M2Tile] 位置表に遠景を記録: {updated} 件");
        }

        /// <summary>屋上を歩くための当たり判定。**取り込み時には付けていない**（→ m0-plan.md §3）。</summary>
        private static int AddColliders()
        {
            int count = 0;

            foreach (MeshFilter filter in UnityEngine.Object.FindObjectsByType<MeshFilter>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (filter.sharedMesh == null) continue;
                if (filter.GetComponent<MeshCollider>() != null) { count++; continue; }

                var collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                count++;
            }

            return count;
        }

        private static Bounds MeasureBounds()
        {
            var renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.one);

            Bounds bounds = renderers[0].bounds;
            foreach (MeshRenderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        /// <summary>
        /// タイルのシーンをBuild Settingsに登録する。
        /// **登録していないシーンは<see cref="UnityEngine.SceneManagement.SceneManager"/>から読めない。**
        /// </summary>
        public static void RegisterTilesInBuildSettings(IEnumerable<string> scenePaths)
        {
            var scenes = EditorBuildSettings.scenes.ToList();

            foreach (string path in scenePaths)
            {
                if (scenes.Any(entry => entry.path == path)) continue;
                scenes.Add(new EditorBuildSettingsScene(path, true));
            }

            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
