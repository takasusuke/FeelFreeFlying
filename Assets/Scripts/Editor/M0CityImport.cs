using System;
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
        private static readonly string[] GridCodes =
        {
            "53394525", "53394526",
            "53394535", "53394536",
        };

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
                    $"メッシュコード={string.Join(",", GridCodes)}");

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
            GridCodeList gridCodes = GridCodeList.CreateFromGridCodesStr(GridCodes);

            var config = CityImportConfig.CreateWithAreaSelectResult(
                new AreaSelectResult(
                    new ConfigBeforeAreaSelect(datasetConfig, CoordinateZoneId),
                    gridCodes,
                    AreaSelectResult.ResultReason.Confirm));

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
