using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 未発見の見どころに**細い光の柱**を1本立てる（docs/m3-plan.md §4.1、要件 §2.1）。
    ///
    /// **矢印やミニマップで案内しない。** 目的地を画面の記号で示すと、見るのが街ではなく
    /// 記号になる。柱にするのは、遠景タイルが無地の箱である（→ m2-plan.md §4.6）以上、
    /// 遠くから読み取れるのが輪郭と光しかないため。
    ///
    /// **近づくと消える。** 消えた先に建物そのものが見えていれば案内は役目を終えていて、
    /// 柱が残っているほうが邪魔になる。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpotBeacons : MonoBehaviour
    {
        [Tooltip("この人からの距離で判定する。未設定ならFlightControllerを探す")]
        [SerializeField] private Transform viewer;

        [Tooltip("柱のマテリアル。**Editor側で作った資産を渡す**——実行時に Shader.Find で" +
                 "作るとビルドからシェーダが落ちることがある")]
        [SerializeField] private Material beaconMaterial;

        [SerializeField] private string city = SpotCatalog.DefaultCity;

        [Tooltip("この距離より遠い柱は出さない (m)。遠景タイルの距離に合わせる")]
        [SerializeField, Min(0f)] private float visibleDistance = 6000f;

        [Tooltip("この距離まで近づいたら柱を消し始める (m)")]
        [SerializeField, Min(0f)] private float fadeNearDistance = 400f;

        [Tooltip("柱が完全に見える距離 (m)。ここから近づくほど薄くなる")]
        [SerializeField, Min(0f)] private float fadeFarDistance = 1200f;

        [Tooltip("スポットの広がりにこれを足した距離まで入ったら「見つけた」とする (m)")]
        [SerializeField, Min(0f)] private float discoverMargin = 80f;

        [Tooltip("柱の高さ (m)。街のいちばん高い建物（240m）より高くする")]
        [SerializeField, Min(1f)] private float pillarHeight = 420f;

        [Tooltip("柱の太さ (m)。**遠いほど太くする**——細いままだと1画素を割ってちらつく")]
        [SerializeField, Min(0.1f)] private float pillarWidth = 7f;

        [Tooltip("同時に出す柱の本数。近いものから")]
        [SerializeField, Min(1)] private int maxVisible = 32;

        [Tooltip("種類と距離を画面に出す。**名前は出さない**（→ m3-plan.md §1.2）")]
        [SerializeField] private bool showStatus = true;

        private readonly List<Beacon> beacons = new List<Beacon>();
        private readonly HashSet<string> discovered = new HashSet<string>();

        private SpotCatalog catalog;
        private Material runtimeMaterial;
        private MaterialPropertyBlock properties;
        private Mesh pillarMesh;
        private GUIStyle style;
        private string notice;
        private float noticeTime;

        /// <summary>いくつ見つけたか。**記録の置き場所はM5で決める**（→ m3-plan.md §7）。</summary>
        public int DiscoveredCount => discovered.Count;

        public int SpotCount => catalog?.Spots.Count ?? 0;

        private sealed class Beacon
        {
            public SpotCatalog.Spot Spot;
            public Transform Transform;
            public MeshRenderer Renderer;
        }

        private void Awake()
        {
            catalog = SpotCatalog.Load(city);

            if (viewer == null)
            {
                var controller = FindAnyObjectByType<FlightController>();
                if (controller != null) viewer = controller.transform;
            }

            if (catalog.Spots.Count == 0) return;

            properties = new MaterialPropertyBlock();
            pillarMesh = BuildPillarMesh();
            runtimeMaterial = CreateRuntimeMaterial();

            foreach (SpotCatalog.Spot spot in catalog.Spots) beacons.Add(CreateBeacon(spot));

            Debug.Log($"[SpotBeacons] 見どころ {catalog.Spots.Count} 個を読み込みました（{city}）");
        }

        private void Update()
        {
            if (viewer == null || beacons.Count == 0) return;

            Vector3 eye = viewer.position;

            // **近い順に本数を絞る。** 都市が増えても画面に出る柱の数は変わらないようにする
            int shown = 0;

            beacons.Sort((a, b) =>
                SquaredDistance(a.Spot.position, eye).CompareTo(SquaredDistance(b.Spot.position, eye)));

            foreach (Beacon beacon in beacons)
            {
                float distance = Vector3.Distance(beacon.Spot.position, eye);

                if (!discovered.Contains(beacon.Spot.id) &&
                    distance <= beacon.Spot.radius + discoverMargin)
                {
                    discovered.Add(beacon.Spot.id);
                    notice = $"{beacon.Spot.Label}を見つけた（{discovered.Count}/{catalog.Spots.Count}）";
                    noticeTime = Time.time;
                }

                float alpha = discovered.Contains(beacon.Spot.id) || shown >= maxVisible
                    ? 0f
                    : Alpha(distance);

                if (alpha <= 0.001f)
                {
                    if (beacon.Renderer.enabled) beacon.Renderer.enabled = false;
                    continue;
                }

                shown++;

                // 遠いほど太く。**見かけの太さを保つ**ためで、実際に太くしたいわけではない
                float width = Mathf.Max(pillarWidth, distance * 0.0025f);
                beacon.Transform.localScale = new Vector3(width, pillarHeight, width);

                properties.SetColor(BaseColorId, ColorFor(beacon.Spot, alpha));
                beacon.Renderer.SetPropertyBlock(properties);
                beacon.Renderer.enabled = true;
            }
        }

        /// <summary>
        /// 近づくほど薄く、遠すぎても薄く。
        /// **近くで消すのが本題**——建物そのものを見せるために立てている柱なので、
        /// 見えた時点で退く（→ m3-plan.md §4.1）。
        /// </summary>
        private float Alpha(float distance)
        {
            if (distance >= visibleDistance) return 0f;

            float near = Mathf.InverseLerp(fadeNearDistance, fadeFarDistance, distance);
            float far = Mathf.InverseLerp(visibleDistance, visibleDistance * 0.75f, distance);

            return Mathf.Clamp01(near) * Mathf.Clamp01(far);
        }

        /// <summary>
        /// 種類ごとの色。**5種類を見分けられる必要はない**——
        /// 「あそこに何かある」が伝われば足りるので、彩度は低めに揃える。
        /// </summary>
        private static Color ColorFor(SpotCatalog.Spot spot, float alpha)
        {
            Color color = spot.Kind switch
            {
                SpotCatalog.SpotKind.Height => new Color(0.65f, 0.85f, 1f),
                SpotCatalog.SpotKind.Usage => new Color(1f, 0.85f, 0.6f),
                SpotCatalog.SpotKind.Roof => new Color(0.75f, 1f, 0.8f),
                SpotCatalog.SpotKind.Void => new Color(0.8f, 0.95f, 0.7f),
                _ => new Color(1f, 0.8f, 0.85f),
            };

            color.a = alpha;
            return color;
        }

        private Beacon CreateBeacon(SpotCatalog.Spot spot)
        {
            var beaconObject = new GameObject($"Beacon_{spot.id}");
            beaconObject.transform.SetParent(transform, false);
            beaconObject.transform.position = spot.position;

            beaconObject.AddComponent<MeshFilter>().sharedMesh = pillarMesh;

            var renderer = beaconObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = runtimeMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.enabled = false;

            return new Beacon
            {
                Spot = spot,
                Transform = beaconObject.transform,
                Renderer = renderer,
            };
        }

        /// <summary>
        /// 十字に組んだ2枚の板。**どの方向から見ても幅が変わらない**ようにするため。
        /// 円柱にしないのは、真横から見た時に光が濃くなるのを避けるのと、面を減らすため。
        /// 原点が根元で、+Y方向に1の高さ——スケールでそのまま高さになる。
        /// </summary>
        private static Mesh BuildPillarMesh()
        {
            var mesh = new Mesh { name = "SpotBeaconPillar" };

            mesh.SetVertices(new List<Vector3>
            {
                new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f),
                new Vector3(0.5f, 1f, 0f), new Vector3(-0.5f, 1f, 0f),
                new Vector3(0f, 0f, -0.5f), new Vector3(0f, 0f, 0.5f),
                new Vector3(0f, 1f, 0.5f), new Vector3(0f, 1f, -0.5f),
            });

            // 縦のUVで上を薄くする（マテリアル側のグラデーション）
            mesh.SetUVs(0, new List<Vector2>
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
            });

            // 裏からも見えるように両面ぶんの三角形を持たせる。**両面表示のシェーダに頼らない**
            mesh.SetTriangles(new[]
            {
                0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3,
                4, 6, 5, 4, 7, 6, 4, 5, 6, 4, 6, 7,
            }, 0);

            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// 上ほど薄くなる帯を1枚のテクスチャで作る。
        /// **マテリアル資産のほうは色と混ぜ方だけを持つ**ので、テクスチャは実行時に作ってよい
        /// （シェーダと違い、テクスチャはビルドから落ちようがない）。
        /// </summary>
        private Material CreateRuntimeMaterial()
        {
            if (beaconMaterial == null)
            {
                Debug.LogWarning("[SpotBeacons] マテリアルが設定されていません。柱は出ません。");
                return null;
            }

            var material = new Material(beaconMaterial);
            const int height = 64;
            var texture = new Texture2D(1, height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                name = "SpotBeaconGradient",
            };

            for (int y = 0; y < height; y++)
            {
                float t = y / (float)(height - 1);

                // 根元は少し抑え、中ほどで最も濃く、上端で消える。
                // 根元まで濃いと屋上に張り付いた棒に見える
                float alpha = Mathf.Sin(Mathf.Pow(t, 0.65f) * Mathf.PI) * 0.9f + 0.1f;
                texture.SetPixel(0, y, new Color(1f, 1f, 1f, Mathf.Clamp01(alpha)));
            }

            texture.Apply();
            material.mainTexture = texture;
            return material;
        }

        private static float SquaredDistance(Vector3 a, Vector3 b) => (a - b).sqrMagnitude;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        /// <summary>
        /// **種類と距離だけ**を出す（→ m3-plan.md §4）。建物の名前は持っていないし、出さない。
        /// </summary>
        private void OnGUI()
        {
            if (!showStatus || catalog == null || catalog.Spots.Count == 0 || viewer == null) return;

            style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                normal = { textColor = new Color(0.85f, 0.95f, 1f, 0.9f) },
            };

            SpotCatalog.Spot nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (Beacon beacon in beacons)
            {
                if (discovered.Contains(beacon.Spot.id)) continue;

                float distance = Vector3.Distance(beacon.Spot.position, viewer.position);
                if (distance >= nearestDistance) continue;

                nearest = beacon.Spot;
                nearestDistance = distance;
            }

            string line = nearest == null
                ? $"見どころ {discovered.Count}/{catalog.Spots.Count}"
                : $"見どころ {discovered.Count}/{catalog.Spots.Count}　" +
                  $"いちばん近い: {nearest.Label} {FormatDistance(nearestDistance)}";

            GUI.Label(new Rect(24f, Screen.height - 78f, 900f, 24f), line, style);

            if (!string.IsNullOrEmpty(notice) && Time.time - noticeTime < 3f)
            {
                GUI.Label(new Rect(0f, Screen.height * 0.36f, Screen.width, 30f), notice,
                    new GUIStyle(style) { alignment = TextAnchor.MiddleCenter, fontSize = 22 });
            }
        }

        private static string FormatDistance(float meters) =>
            meters >= 1000f ? $"{meters / 1000f:F1} km" : $"{meters:F0} m";
    }
}
