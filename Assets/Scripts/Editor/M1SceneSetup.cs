using System.IO;
using System.Linq;
using FeelFreeFlying.Benchmark;
using FeelFreeFlying.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FeelFreeFlying.EditorTools
{
    /// <summary>
    /// M1の試遊シーンを組む（docs/m1-plan.md）。
    ///
    /// 要件では「街は仮の箱でよい」（`requirements.md` §8）が、**M0で本物の新宿が手元にある**ので
    /// それを使う。箱の羅列より、実際に飛ぶ街で判定したほうが判断が確かになる。
    ///
    /// M0の計測シーンを土台にして、計測用のリグ（軌道とRunner）を外し、操作できる機体に差し替える。
    /// 手で組まないのは、都市を入れ直すたびに作り直すことになるため。
    ///
    ///   Unity.exe -projectPath . -batchmode -quit -logFile <ログ> `
    ///     -executeMethod FeelFreeFlying.EditorTools.M1SceneSetup.CreateFlightScene
    /// </summary>
    public static class M1SceneSetup
    {
        private const string SourceScene = "Assets/Scenes/M0Benchmark.unity";
        private const string FlightScene = "Assets/Scenes/M1Flight.unity";
        private const string SuitMaterialPath = "Assets/Settings/Rendering/PlaceholderFlyerSuit.mat";
        private const string CapeMaterialPath = "Assets/Settings/Rendering/PlaceholderFlyerCape.mat";
        private const string WaterMaterialPath = "Assets/Settings/Rendering/PlaceholderWater.mat";

        /// <summary>海面の広さ（1辺・メートル）。カメラの遠クリップより広くないと端が見える。</summary>
        private const float OceanRadiusMeters = 40000f;

        /// <summary>海面を街の最下点からどれだけ下げるか (m)。</summary>
        private const float SeaLevelBelowCity = 2f;

        /// <summary>街を見下ろせて、かつ建物が迫って見える高さ。</summary>
        private const float StartAltitude = 180f;

        /// <summary>街の中心から手前に離す距離。最初の数秒で街に「入っていく」ようにする。</summary>
        private const float StartDistance = 900f;

        [MenuItem("Tools/FeelFreeFlying/M1: 試遊シーンを作る")]
        public static void CreateFlightScene()
        {
            if (!File.Exists(SourceScene))
            {
                Debug.LogError(
                    $"[M1Setup] {SourceScene} がありません。先に都市を取り込んでください" +
                    "（Tools > FeelFreeFlying > M0: 新宿を取り込む）。");
                EditorApplication.Exit(1);
                return;
            }

            Scene scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);

            Bounds cityBounds = CalculateCityBounds();
            RemoveBenchmarkRig();

            int colliders = AddCityColliders();
            CreateOcean(cityBounds);

            // 街（灰色）に対して沈まない明るさにする。暗い色だと自分の姿を見失う
            Material suit = LoadOrCreateMaterial(SuitMaterialPath, new Color(0.22f, 0.42f, 0.75f));
            Material cape = LoadOrCreateMaterial(CapeMaterialPath, new Color(0.62f, 0.14f, 0.16f));
            GameObject craft = CreateCraft(cityBounds, suit, cape);
            SetUpCamera(craft.GetComponent<FlightController>());

            var hud = new GameObject("FlightHud");
            hud.AddComponent<FlightHud>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, FlightScene);
            AddSceneToBuildSettings(FlightScene);

            Debug.Log(
                $"[M1Setup] 作成: {FlightScene}\n" +
                $"[M1Setup] 街の広がり: {cityBounds.size.x:F0} x {cityBounds.size.z:F0} m / " +
                $"開始地点: {craft.transform.position} / 当たり判定: {colliders} 件");
        }

        /// <summary>街の範囲。開始地点を街に対して決めるために使う。</summary>
        private static Bounds CalculateCityBounds()
        {
            var renderers = Object.FindObjectsByType<MeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.one * 100f);

            Bounds bounds = renderers[0].bounds;
            foreach (MeshRenderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        private static void RemoveBenchmarkRig()
        {
            foreach (FlightBenchmarkRunner runner in Object.FindObjectsByType<FlightBenchmarkRunner>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(runner);
            }

            foreach (FlightBenchmarkPath path in Object.FindObjectsByType<FlightBenchmarkPath>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(path.gameObject);
            }
        }

        /// <summary>
        /// 都市に当たり判定を付ける。**屋上を歩くために要る。**
        ///
        /// インポート時はMesh Colliderを付けていない（飛ぶだけなら不要で、メモリと生成時間が増える
        /// → `m0-plan.md` §3）。歩けるようにする分だけここで付ける。
        /// **地域単位で取り込んでいれば数個で済む**（主要地物単位だと8千個を超えるので、
        /// その粒度で歩かせたくなったら別の手が要る）。
        ///
        /// 飛行中は当たり判定を無視する（機体側のCharacterControllerを切ってある）ので、
        /// 建物をすり抜けて飛ぶ挙動は変わらない。
        /// </summary>
        private static int AddCityColliders()
        {
            int count = 0;

            foreach (MeshFilter filter in Object.FindObjectsByType<MeshFilter>(
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

        /// <summary>
        /// 取り込んだ範囲の外側を海で埋める。
        ///
        /// **街の外へ出られてしまうため。** 取り込んだのは2km四方だけで、その外は
        /// 何も無い空間になる。地面が消えると「世界の果て」が見えてしまい、
        /// 飛んでいる場所が箱庭だと分かってしまう。海なら**端が自然に見え、
        /// 引き返す理由にもなる**（都市を足す時は、その都市の周りだけ地面が生える）。
        ///
        /// 海面は街の最下点のわずかに下に置く。地形の縁が海に落ちる崖になり、
        /// 島のように見える。**水の表現は作り込まない**（M5の課題）。
        /// </summary>
        private static void CreateOcean(Bounds cityBounds)
        {
            Material water = LoadOrCreateMaterial(WaterMaterialPath, new Color(0.05f, 0.16f, 0.26f));

            GameObject ocean = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ocean.name = "Ocean";
            ocean.transform.position = new Vector3(
                cityBounds.center.x, cityBounds.min.y - SeaLevelBelowCity, cityBounds.center.z);

            // Planeは1辺10mなので、10km四方にするには1000倍。カメラの遠クリップ(20km)より広い
            ocean.transform.localScale = Vector3.one * OceanRadiusMeters / 10f;

            if (water != null)
            {
                var renderer = ocean.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = water;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            Object.DestroyImmediate(ocean.GetComponent<Collider>());
        }

        /// <summary>
        /// 飛ぶのは航空機ではなく**人**（腕を前に伸ばしてマントをなびかせる姿勢）。
        /// 見た目は仮だが、**一人称にした時に自分の腕が視界に入る**位置関係だけは合わせてある。
        /// これが無いと一人称が「浮いているカメラ」になり、身体で飛んでいる感じが出ない。
        ///
        /// 前方は+Z、上は+Y。全長は指先から足先まで約1.8m。
        /// </summary>
        private static GameObject CreateCraft(Bounds cityBounds, Material suit, Material cape)
        {
            Vector3 center = cityBounds.center;
            var start = new Vector3(center.x, StartAltitude, center.z - StartDistance);

            var flyer = new GameObject("Flyer");
            flyer.transform.SetPositionAndRotation(start, Quaternion.identity); // 街の方（+Z）を向く

            flyer.AddComponent<FlightInput>();

            // 歩行時だけ有効にする当たり判定。飛行中は切ってあるので建物をすり抜ける
            CharacterController walker = flyer.AddComponent<CharacterController>();
            walker.radius = 0.35f;
            walker.height = 1.7f;
            walker.center = Vector3.zero;
            walker.slopeLimit = 50f;
            walker.stepOffset = 0.4f;

            FlightController controller = flyer.AddComponent<FlightController>();

            // 海面の高さを渡す。飛行の下限と「海に落ちたら飛行へ戻す」判定がこれを基準にする
            var serialized = new SerializedObject(controller);
            serialized.FindProperty("seaLevel").floatValue = cityBounds.min.y - SeaLevelBelowCity;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var alongZ = new Vector3(90f, 0f, 0f); // カプセルの軸をY（既定）から進行方向Zへ倒す

            AddPart(flyer.transform, "Torso", PrimitiveType.Capsule,
                new Vector3(0f, 0f, 0.05f), alongZ, new Vector3(0.34f, 0.42f, 0.34f), suit);

            AddPart(flyer.transform, "Head", PrimitiveType.Sphere,
                new Vector3(0f, 0.13f, 0.5f), Vector3.zero, new Vector3(0.26f, 0.26f, 0.26f), suit);

            AddPart(flyer.transform, "ArmLeft", PrimitiveType.Capsule,
                new Vector3(-0.19f, 0.02f, 0.58f), alongZ, new Vector3(0.12f, 0.32f, 0.12f), suit);
            AddPart(flyer.transform, "ArmRight", PrimitiveType.Capsule,
                new Vector3(0.19f, 0.02f, 0.58f), alongZ, new Vector3(0.12f, 0.32f, 0.12f), suit);

            AddPart(flyer.transform, "LegLeft", PrimitiveType.Capsule,
                new Vector3(-0.1f, 0f, -0.6f), alongZ, new Vector3(0.14f, 0.38f, 0.14f), suit);
            AddPart(flyer.transform, "LegRight", PrimitiveType.Capsule,
                new Vector3(0.1f, 0f, -0.6f), alongZ, new Vector3(0.14f, 0.38f, 0.14f), suit);

            // マント。速度感は自分の身体の一部が後ろへ流れているほうが分かりやすい。
            // **幅と角度は控えめにする。** 大きく水平に広げると、後方から見下ろすカメラに対して
            // 面が正対し、身体を完全に隠す（実際に一度そうなった）
            AddPart(flyer.transform, "Cape", PrimitiveType.Cube,
                new Vector3(0f, -0.1f, -0.6f), new Vector3(-10f, 0f, 0f), new Vector3(0.3f, 0.02f, 0.8f), cape);

            return flyer;
        }

        private static void AddPart(Transform parent, string name, PrimitiveType primitive,
            Vector3 localPosition, Vector3 localEuler, Vector3 scale, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(primitive);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = Quaternion.Euler(localEuler);
            part.transform.localScale = scale;
            if (material != null) part.GetComponent<MeshRenderer>().sharedMaterial = material;

            // 当たり判定は持たせない。都市側にコライダーが無く（→ m0-plan.md §3）、
            // 落下も墜落も作らない方針のため（CLAUDE.md 不変条件3）
            Object.DestroyImmediate(part.GetComponent<Collider>());
        }

        private static void SetUpCamera(FlightController target)
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
                camera = cameraObject.AddComponent<Camera>();
            }

            camera.nearClipPlane = 1f;
            camera.farClipPlane = 20000f;

            FlightCamera follow = camera.GetComponent<FlightCamera>();
            if (follow == null) follow = camera.gameObject.AddComponent<FlightCamera>();

            var serialized = new SerializedObject(follow);
            serialized.FindProperty("target").objectReferenceValue = target;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Material LoadOrCreateMaterial(string path, Color color)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogWarning("[M1Setup] URPのLitシェーダが見つかりません。既定マテリアルを使います。");
                return null;
            }

            var material = new Material(shader) { color = color };
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            return material;
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            if (scenes.Any(entry => entry.path == scenePath)) return;

            EditorBuildSettings.scenes = scenes
                .Append(new EditorBuildSettingsScene(scenePath, true))
                .ToArray();
        }
    }
}
