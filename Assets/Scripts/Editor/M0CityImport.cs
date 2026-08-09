using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PLATEAU.CityImport.AreaSelector;
using PLATEAU.CityImport.Config;
using PLATEAU.CityImport.Config.PackageImportConfigs;
using PLATEAU.CityImport.Import;
using PLATEAU.CityInfo;
using PLATEAU.Dataset;
using PLATEAU.PolygonMesh;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// 新宿のPLATEAUデータをスクリプトから取り込む（docs/m0-plan.md §3）。
    ///
    /// **手作業のインポートは3回失われた**（属性オンで壊れる / 範囲が広すぎてUnityが異常終了 /
    /// 保存されていなかった）。1回30分以上かかる操作を毎回GUIでやり直すのは割に合わないうえ、
    /// 設定が人の記憶に残るだけでは同じ条件を再現できない。設定値をこのファイルに固定する。
    ///
    /// `CLAUDE.md` の不変条件2「都市データの取り込みを手作業にしない」も同じことを要求している。
    ///
    /// バッチモード（**-quit を付けない**。付けると非同期の続きが走る前にUnityが終了する。
    /// 完了時にこちら側で <see cref="EditorApplication.Exit"/> する）:
    ///
    ///   Unity.exe -projectPath . -batchmode -logFile <ログ> `
    ///     -executeMethod FeelFreeFlying.EditorTools.M0CityImport.ImportFromCommandLine `
    ///     -ffimport-granularity area
    /// </summary>
    public static class M0CityImport
    {
        /// <summary>初回のサーバーインポートで取得したCityGML（gitignore済み・再取得可能）。</summary>
        private const string DatasetPath = "Data/Plateau/13104_shinjuku-ku_pref_2025_citygml_1_op";

        /// <summary>
        /// 新宿駅周辺の3次メッシュ4枚（約2km四方）。**粒度を比較する以上、範囲は固定でなければならない。**
        /// 広げると主要地物単位でUnityが落ちる（6.4 x 6.8kmで異常終了した）。
        /// </summary>
        private static readonly string[] DefaultGridCodes =
        {
            "53394525", "53394526",
            "53394535", "53394536",
        };

        /// <summary>取り込む範囲。`-ffimport-grid` で上書きできる（範囲の限界を測るため）。</summary>
        private static string[] gridCodes = DefaultGridCodes;

        /// <summary>
        /// 原点をどの範囲の中心に置くか。
        ///
        /// **タイルごとに取り込むと、SDKは毎回その範囲の中心を原点にする**
        /// （`CityImportConfig.CreateWithAreaSelectResult`）。そのままだと
        /// 4枚のタイルが全部原点に重なり、街が積み重なった1枚の塊になる。
        /// 街全体を基準にすれば、タイルは実際の位置関係のまま並ぶ。
        /// </summary>
        private static string[] referenceGridCodes = DefaultGridCodes;

        /// <summary>平面直角座標系9系（東京）。</summary>
        private const int CoordinateZoneId = 9;

        /// <summary>
        /// 建築物のLOD。**形状はLOD2を使う**（`requirements.md` §5・2026-08-09決定）。
        /// LOD1は箱、LOD0は底面ポリゴンで箱にすらならない。
        /// 実写テクスチャは使わないので<see cref="Run"/>のincludeTextureは既定でfalseのまま。
        /// </summary>
        private const int DefaultBuildingLod = 2;

        private const string ScenePath = "Assets/Scenes/M0Benchmark.unity";
        private const string GranularityArg = "-ffimport-granularity";
        private const string LodArg = "-ffimport-lod";
        private const string TextureArg = "-ffimport-texture";
        private const string TextureResolutionArg = "-ffimport-texres";
        private const string GridArg = "-ffimport-grid";

        [MenuItem("Tools/FeelFreeFlying/M0: 新宿を取り込む（主要地物単位）")]
        public static void ImportPerPrimaryFeature() =>
            Run(MeshGranularity.PerPrimaryFeatureObject, DefaultBuildingLod, false,
                TexturePackingResolution.W2048H2048, exitWhenDone: false);

        [MenuItem("Tools/FeelFreeFlying/M0: 新宿を取り込む（地域単位）")]
        public static void ImportPerCityModelArea() =>
            Run(MeshGranularity.PerCityModelArea, DefaultBuildingLod, false,
                TexturePackingResolution.W2048H2048, exitWhenDone: false);

        [MenuItem("Tools/FeelFreeFlying/M1: 新宿を取り込む（LOD2・実写テクスチャ）")]
        public static void ImportTexturedLod2() =>
            Run(MeshGranularity.PerPrimaryFeatureObject, 2, true,
                TexturePackingResolution.W2048H2048, exitWhenDone: false);

        /// <summary>バッチモードからの入口。</summary>
        public static void ImportFromCommandLine()
        {
            MeshGranularity granularity = MeshGranularity.PerPrimaryFeatureObject;
            int lod = DefaultBuildingLod;
            bool includeTexture = false;
            TexturePackingResolution textureResolution = TexturePackingResolution.W2048H2048;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case GranularityArg:
                        switch (args[i + 1])
                        {
                            case "area":
                                granularity = MeshGranularity.PerCityModelArea;
                                break;
                            case "primary":
                                granularity = MeshGranularity.PerPrimaryFeatureObject;
                                break;
                            default:
                                Debug.LogError(
                                    $"[M0Import] {GranularityArg} は area か primary。受け取った値: {args[i + 1]}");
                                EditorApplication.Exit(1);
                                return;
                        }
                        break;

                    case LodArg:
                        if (!int.TryParse(args[i + 1], out lod))
                        {
                            Debug.LogError($"[M0Import] {LodArg} は数値。受け取った値: {args[i + 1]}");
                            EditorApplication.Exit(1);
                            return;
                        }
                        break;

                    case TextureArg:
                        includeTexture = args[i + 1] == "on";
                        break;

                    case TextureResolutionArg:
                        switch (args[i + 1])
                        {
                            // SDKが持つのはこの3段階だけ（1024は無い）
                            case "2048": textureResolution = TexturePackingResolution.W2048H2048; break;
                            case "4096": textureResolution = TexturePackingResolution.W4096H4096; break;
                            case "8192": textureResolution = TexturePackingResolution.W8192H8192; break;
                            default:
                                Debug.LogError($"[M0Import] {TextureResolutionArg} は 2048/4096/8192。受け取った値: {args[i + 1]}");
                                EditorApplication.Exit(1);
                                return;
                        }
                        break;
                }
            }

            Run(granularity, lod, includeTexture, textureResolution, exitWhenDone: true);
        }

        private static void Run(MeshGranularity granularity, int lod, bool includeTexture,
            TexturePackingResolution textureResolution, bool exitWhenDone)
        {
            // 非同期の完了を待たずに戻る。バッチモードでは -quit を付けずに実行し、
            // 完了時にこちらから終了する（メインスレッドを止めると続きが動かないため）。
            _ = RunAsync(granularity, lod, includeTexture, textureResolution, exitWhenDone);
        }

        private static async Task RunAsync(MeshGranularity granularity, int lod, bool includeTexture,
            TexturePackingResolution textureResolution, bool exitWhenDone)
        {
            try
            {
                string datasetFullPath = Path.GetFullPath(DatasetPath);
                if (!Directory.Exists(datasetFullPath))
                {
                    throw new DirectoryNotFoundException(
                        $"CityGMLが見つかりません: {datasetFullPath}\n" +
                        "docs/m0-plan.md §3 の手順でサーバーから取得してください。");
                }

                Scene scene = OpenBenchmarkScene();
                int removed = RemoveExistingCityModels();
                if (removed > 0) Debug.Log($"[M0Import] 既存の都市モデルを削除: {removed} 件");

                CityImportConfig config = BuildConfig(datasetFullPath, granularity, lod, includeTexture, textureResolution);
                Debug.Log(
                    $"[M0Import] 開始: 粒度={granularity} / LOD={lod} / テクスチャ={(includeTexture ? "あり" : "なし")} / " +
                    $"メッシュコード={string.Join(",", gridCodes)}");

                await CityImporter.ImportAsync(config, null, null);

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, ScenePath);

                var renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                Debug.Log(
                    $"[M0Import] 完了: 粒度={granularity} / MeshRenderer={renderers.Length} 件 / " +
                    $"保存={ScenePath}");

                if (exitWhenDone) EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M0Import] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// **LOD2があればLOD2、無ければLOD1**で取り込む（docs/m0-plan.md §3.3）。
        ///
        /// LOD2だけを指定すると、LOD2を持たない建物は**黙って消える**（新宿で625棟）。
        /// SDKのLOD指定は範囲の下限・上限しか持たず、しかも範囲を1〜2にすると
        /// **両方のLODが二重に生成される**ので、「建物ごとに最良のLODを選ぶ」ができない。
        ///
        /// そこで2回取り込む。LOD2を入れてから、LOD1を重ねて入れ、
        /// **LOD2で既に入っている建物の複製だけを消す**。
        ///
        /// **主要地物単位でしか成立しない。** 地域単位はメッシュが結合済みで、
        /// 建物ごとに残す・消すの判断ができない。
        /// </summary>
        public static void ImportWithLodFallbackFromCommandLine()
        {
            bool includeTexture = false;
            var textureResolution = TexturePackingResolution.W2048H2048;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == GridArg) gridCodes = args[i + 1].Split(',');
                if (args[i] == TextureArg) includeTexture = args[i + 1] == "on";
                if (args[i] == TextureResolutionArg && args[i + 1] == "4096")
                {
                    textureResolution = TexturePackingResolution.W4096H4096;
                }
            }

            _ = RunFallbackAsync(includeTexture, textureResolution, exitWhenDone: true);
        }

        [MenuItem("Tools/FeelFreeFlying/M2: 新宿を取り込む（LOD2 + 無ければLOD1）")]
        public static void ImportWithLodFallback() =>
            _ = RunFallbackAsync(false, TexturePackingResolution.W2048H2048, exitWhenDone: false);

        private static async Task RunFallbackAsync(bool includeTexture,
            TexturePackingResolution textureResolution, bool exitWhenDone) =>
            await ImportFallbackInto(ScenePath, includeTexture, textureResolution, exitWhenDone);

        /// <summary>
        /// LOD2優先＋LOD1補完の取り込みを、**指定したシーンへ**行う。
        /// タイル単位のパイプライン（docs/m2-plan.md §2）から1メッシュずつ呼ぶために切り出した。
        /// </summary>
        public static async Task<int> ImportFallbackInto(string targetScenePath, bool includeTexture,
            TexturePackingResolution textureResolution, bool exitWhenDone)
        {
            try
            {
                string datasetFullPath = Path.GetFullPath(DatasetPath);
                if (!Directory.Exists(datasetFullPath))
                {
                    throw new DirectoryNotFoundException($"CityGMLが見つかりません: {datasetFullPath}");
                }

                Scene scene = OpenOrCreateScene(targetScenePath);
                RemoveExistingCityModels();

                const MeshGranularity granularity = MeshGranularity.PerPrimaryFeatureObject;

                Debug.Log("[M0Import] LOD2を取り込みます");
                await CityImporter.ImportAsync(
                    BuildConfig(datasetFullPath, granularity, 2, includeTexture, textureResolution), null, null);

                var lod2Buildings = new Dictionary<string, GameObject>();
                foreach (MeshRenderer renderer in FindBuildings()) lod2Buildings[renderer.name] = renderer.gameObject;
                Debug.Log($"[M0Import] LOD2: {lod2Buildings.Count} 棟");

                Debug.Log("[M0Import] LOD1を重ねて取り込みます（LOD2に無い建物のため）");
                await CityImporter.ImportAsync(
                    BuildConfig(datasetFullPath, granularity, 1, includeTexture, textureResolution), null, null);

                // LOD1側の複製を消す。**LOD2で入っている建物と同じgmlIDのものだけ**
                int duplicates = 0;
                foreach (MeshRenderer renderer in FindBuildings())
                {
                    if (!lod2Buildings.TryGetValue(renderer.name, out GameObject kept)) continue;
                    if (renderer.gameObject == kept) continue;

                    UnityEngine.Object.DestroyImmediate(renderer.gameObject);
                    duplicates++;
                }

                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene, targetScenePath);

                int total = FindBuildings().Count;
                Debug.Log(
                    $"[M0Import] 完了: 建物 {total} 棟（LOD2 {lod2Buildings.Count} 棟 + " +
                    $"LOD1で補った {total - lod2Buildings.Count} 棟 / 重複削除 {duplicates} 棟）→ {targetScenePath}");

                if (exitWhenDone) EditorApplication.Exit(0);
                return total;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[M0Import] 失敗: {exception}");
                if (exitWhenDone) EditorApplication.Exit(1);
                return 0;
            }
        }

        /// <summary>タイルのシーンは初回に作る。計測シーンと違ってリグは要らない。</summary>
        private static Scene OpenOrCreateScene(string path)
        {
            if (path == ScenePath) return OpenBenchmarkScene();

            if (File.Exists(path)) return EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

            Scene created = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            EditorSceneManager.SaveScene(created, path);
            return created;
        }

        /// <summary>
        /// タイル1枚を取り込む。
        /// **原点は街全体（<paramref name="allGridCodes"/>）の中心に置く**ので、
        /// 別々に取り込んだタイルを並べても位置が合う。
        /// </summary>
        public static async Task<int> ImportTile(string gridCode, string targetScenePath,
            string[] allGridCodes, bool includeTexture = false)
        {
            gridCodes = new[] { gridCode };
            referenceGridCodes = allGridCodes;

            // アトラスは2048px。**4096pxだと15棟で+451MB増える**（→ m1-plan.md §6.3）
            return await ImportFallbackInto(targetScenePath, includeTexture,
                TexturePackingResolution.W2048H2048, exitWhenDone: false);
        }

        private static List<MeshRenderer> FindBuildings()
        {
            return UnityEngine.Object.FindObjectsByType<MeshRenderer>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(renderer => renderer.name.StartsWith("bldg_"))
                .ToList();
        }

        private static Scene OpenBenchmarkScene()
        {
            if (!File.Exists(ScenePath))
            {
                // クローン直後は計測シーンが無い（シーンはGit管理外 → §4.1）
                M0ProjectSetup.CreateBenchmarkScene();
            }
            return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        /// <summary>前回のインポート結果を消す。残したまま取り込むと二重に描画され、計測値が壊れる。</summary>
        private static int RemoveExistingCityModels()
        {
            var models = UnityEngine.Object.FindObjectsByType<PLATEAUInstancedCityModel>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (PLATEAUInstancedCityModel model in models)
            {
                UnityEngine.Object.DestroyImmediate(model.gameObject);
            }
            return models.Length;
        }

        private static CityImportConfig BuildConfig(string datasetFullPath, MeshGranularity granularity,
            int buildingLod, bool includeTexture, TexturePackingResolution textureResolution)
        {
            var datasetConfig = new DatasetSourceConfigLocal(datasetFullPath);
            GridCodeList codes = GridCodeList.CreateFromGridCodesStr(gridCodes);

            var config = CityImportConfig.CreateWithAreaSelectResult(
                new AreaSelectResult(
                    new ConfigBeforeAreaSelect(datasetConfig, CoordinateZoneId),
                    codes,
                    AreaSelectResult.ResultReason.Confirm));

            // **原点は街全体の中心に固定する。** 既定ではこの取り込み範囲の中心になるため、
            // タイルごとに取り込むと全部が原点に重なる（→ referenceGridCodes）
            GridCodeList referenceCodes = GridCodeList.CreateFromGridCodesStr(referenceGridCodes);
            config.ReferencePoint = referenceCodes.ExtentCenter(CoordinateZoneId);

            foreach (var pair in config.PackageImportConfigDict.ForEachPackagePair.ToArray())
            {
                PredefinedCityModelPackage package = pair.Key;
                PackageImportConfig packageConfig = pair.Value;

                // 建築物と土地起伏だけ。道路・都市設備を入れると負荷の主因が分からなくなる（→ §3）
                bool wanted = package == PredefinedCityModelPackage.Building ||
                              package == PredefinedCityModelPackage.Relief;

                packageConfig.ImportPackage = packageConfig.ImportPackage && wanted;
                if (!packageConfig.ImportPackage) continue;

                packageConfig.MeshGranularity = granularity;
                packageConfig.IncludeTexture = includeTexture;
                packageConfig.EnableTexturePacking = includeTexture;

                // **アトラスの解像度が容量を決める。** 4096pxだとランドマーク15棟で+451MB増えた
                // （シーンに埋め込まれるため圧縮も効かない）。既定を2048pxに下げてある
                packageConfig.TexturePackingResolution = textureResolution;
                packageConfig.DoSetMeshCollider = false;   // 飛ぶだけなので不要
                packageConfig.DoSetAttrInfo = false;       // **オンにするとインポートが壊れる（→ §3.1）**

                // 地面（土地起伏）は航空写真（地理院タイル）を貼る。無地の地面だと現実感が出ず、
                // 公園（新宿御苑）が建物の隙間の空白にしか見えない。
                // SDKの既定でタイルが付く設定になっているので、**テクスチャを有効にするだけでよい**。
                // **出典表示が要る**（国土地理院 → requirements.md §4.2）
                if (package == PredefinedCityModelPackage.Relief) packageConfig.IncludeTexture = true;

                if (package != PredefinedCityModelPackage.Building) continue;

                // 土地起伏はLOD0しか無いので、LODを固定するのは建築物だけ
                int available = packageConfig.LODRange.AvailableMaxLOD;
                int lod = Mathf.Clamp(buildingLod, 0, Mathf.Max(available, 0));
                if (lod != buildingLod)
                {
                    Debug.LogWarning(
                        $"[M0Import] LOD{buildingLod}は{package}に存在しません。LOD{lod}で取り込みます" +
                        $"（このデータの最大LOD={available}）。");
                }
                packageConfig.LODRange = new LODRange(lod, lod, available);
            }

            return config;
        }
    }
}
