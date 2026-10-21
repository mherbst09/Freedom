using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace ViennaAtomicClock.PunchFactory;

/// <summary>Tiny helper to build low-poly meshes.</summary>
internal sealed class MeshBuilder
{
    public MeshGeometry3D Mesh { get; } = new();

    public void Quad(Point3D a, Point3D b, Point3D c, Point3D d, double u0 = 0, double v0 = 0, double u1 = 1, double v1 = 1)
    {
        var i = Mesh.Positions.Count;
        Mesh.Positions.Add(a); Mesh.Positions.Add(b); Mesh.Positions.Add(c); Mesh.Positions.Add(d);
        Mesh.TextureCoordinates.Add(new Point(u0, v1)); Mesh.TextureCoordinates.Add(new Point(u1, v1));
        Mesh.TextureCoordinates.Add(new Point(u1, v0)); Mesh.TextureCoordinates.Add(new Point(u0, v0));
        Mesh.TriangleIndices.Add(i); Mesh.TriangleIndices.Add(i + 1); Mesh.TriangleIndices.Add(i + 2);
        Mesh.TriangleIndices.Add(i); Mesh.TriangleIndices.Add(i + 2); Mesh.TriangleIndices.Add(i + 3);
    }

    /// <summary>Cylinder around the Y axis; texture u goes around, v from top (0) to bottom (1).</summary>
    public void Cylinder(Point3D bottomCenter, double radius, double height, int segments, bool caps = true)
    {
        var start = Mesh.Positions.Count;
        for (var s = 0; s <= segments; s++)
        {
            var angle = 2 * Math.PI * s / segments;
            var x = Math.Sin(angle) * radius;
            var z = Math.Cos(angle) * radius;
            Mesh.Positions.Add(new Point3D(bottomCenter.X + x, bottomCenter.Y + height, bottomCenter.Z + z));
            Mesh.Positions.Add(new Point3D(bottomCenter.X + x, bottomCenter.Y, bottomCenter.Z + z));
            Mesh.Normals.Add(new Vector3D(x, 0, z));
            Mesh.Normals.Add(new Vector3D(x, 0, z));
            var u = (double)s / segments;
            Mesh.TextureCoordinates.Add(new Point(u, 0));
            Mesh.TextureCoordinates.Add(new Point(u, 1));
        }
        for (var s = 0; s < segments; s++)
        {
            var i = start + s * 2;
            Mesh.TriangleIndices.Add(i); Mesh.TriangleIndices.Add(i + 1); Mesh.TriangleIndices.Add(i + 3);
            Mesh.TriangleIndices.Add(i); Mesh.TriangleIndices.Add(i + 3); Mesh.TriangleIndices.Add(i + 2);
        }
        if (!caps) return;
        foreach (var top in new[] { true, false })
        {
            var center = Mesh.Positions.Count;
            var y = bottomCenter.Y + (top ? height : 0);
            Mesh.Positions.Add(new Point3D(bottomCenter.X, y, bottomCenter.Z));
            Mesh.Normals.Add(new Vector3D(0, top ? 1 : -1, 0));
            Mesh.TextureCoordinates.Add(new Point(0.5, top ? 0.02 : 0.98));
            for (var s = 0; s <= segments; s++)
            {
                var angle = 2 * Math.PI * s / segments;
                Mesh.Positions.Add(new Point3D(bottomCenter.X + Math.Sin(angle) * radius, y, bottomCenter.Z + Math.Cos(angle) * radius));
                Mesh.Normals.Add(new Vector3D(0, top ? 1 : -1, 0));
                Mesh.TextureCoordinates.Add(new Point((double)s / segments, top ? 0.02 : 0.98));
            }
            for (var s = 0; s < segments; s++)
            {
                if (top) { Mesh.TriangleIndices.Add(center); Mesh.TriangleIndices.Add(center + 1 + s); Mesh.TriangleIndices.Add(center + 2 + s); }
                else { Mesh.TriangleIndices.Add(center); Mesh.TriangleIndices.Add(center + 2 + s); Mesh.TriangleIndices.Add(center + 1 + s); }
            }
        }
    }

    public void Sphere(Point3D center, double rx, double ry, double rz, int slices, int stacks)
    {
        var start = Mesh.Positions.Count;
        for (var st = 0; st <= stacks; st++)
        {
            var phi = Math.PI * st / stacks;
            for (var sl = 0; sl <= slices; sl++)
            {
                var theta = 2 * Math.PI * sl / slices;
                var n = new Vector3D(Math.Sin(phi) * Math.Sin(theta), Math.Cos(phi), Math.Sin(phi) * Math.Cos(theta));
                Mesh.Positions.Add(new Point3D(center.X + n.X * rx, center.Y + n.Y * ry, center.Z + n.Z * rz));
                Mesh.Normals.Add(n);
                Mesh.TextureCoordinates.Add(new Point((double)sl / slices, (double)st / stacks));
            }
        }
        for (var st = 0; st < stacks; st++)
            for (var sl = 0; sl < slices; sl++)
            {
                var a = start + st * (slices + 1) + sl;
                var b = a + slices + 1;
                Mesh.TriangleIndices.Add(a); Mesh.TriangleIndices.Add(b); Mesh.TriangleIndices.Add(a + 1);
                Mesh.TriangleIndices.Add(a + 1); Mesh.TriangleIndices.Add(b); Mesh.TriangleIndices.Add(b + 1);
            }
    }

    public void Box(Point3D min, Point3D max)
    {
        var (x0, y0, z0, x1, y1, z1) = (min.X, min.Y, min.Z, max.X, max.Y, max.Z);
        Quad(new(x0, y0, z1), new(x1, y0, z1), new(x1, y1, z1), new(x0, y1, z1));
        Quad(new(x1, y0, z0), new(x0, y0, z0), new(x0, y1, z0), new(x1, y1, z0));
        Quad(new(x0, y0, z0), new(x0, y0, z1), new(x0, y1, z1), new(x0, y1, z0));
        Quad(new(x1, y0, z1), new(x1, y0, z0), new(x1, y1, z0), new(x1, y1, z1));
        Quad(new(x0, y1, z1), new(x1, y1, z1), new(x1, y1, z0), new(x0, y1, z0));
        Quad(new(x0, y0, z0), new(x1, y0, z0), new(x1, y0, z1), new(x0, y0, z1));
    }

    public MeshGeometry3D Frozen()
    {
        Mesh.Freeze();
        return Mesh;
    }
}

/// <summary>
/// The studio in 3D (WPF Viewport3D) rendered at 384×216 and scaled up without smoothing – the pixel look. Lights,
/// specular highlights, a swinging and squashing bag and flying gloves are real 3D; particles, hit marks, boxers,
/// decorations and event targets are pixel sprites drawn into the same low-resolution frame.
/// </summary>
internal sealed class PunchScene
{
    public const int W = 384;
    public const int H = 216;
    private const int MaxParticles = 420;
    private const double BagTop = 3.15, BagBottom = 1.35, BagRadius = 0.45, Hinge = 3.75;

    private readonly Viewport3D _viewport = new() { Width = W, Height = H, ClipToBounds = true };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 56, UpDirection = new Vector3D(0, 1, 0) };
    private readonly Model3DGroup _world = new();
    private readonly Model3DGroup _room = new();
    private readonly Model3DGroup _lights = new();
    private readonly GeometryModel3D _bag = new();
    private readonly GeometryModel3D _shadow = new();
    private readonly GeometryModel3D[] _gloves = { new(), new() };
    private readonly Transform3DGroup _bagTransform = new();
    private readonly AxisAngleRotation3D _swingSide = new(new Vector3D(0, 0, 1), 0);
    private readonly AxisAngleRotation3D _swingDepth = new(new Vector3D(1, 0, 0), 0);
    private readonly ScaleTransform3D _squash = new(1, 1, 1, 0, (BagTop + BagBottom) / 2, 0);
    private readonly TranslateTransform3D[] _glovePositions = { new(), new() };
    private readonly ScaleTransform3D[] _gloveScales = { new(), new() };
    private readonly TranslateTransform3D _shadowPosition = new();
    private readonly SolidColorBrush _furyGlow = new(Color.FromArgb(0, 255, 120, 30));
    private readonly PointLight _furyLight = new(Colors.Black, new Point3D(0, 2.3, 1.2)) { Range = 6 };
    private readonly Grid _root = new() { Width = W, Height = H, ClipToBounds = true };
    private readonly SpriteLayer _sprites;
    private SceneState _state = new(Array.Empty<int>(), false, null);
    private readonly Dictionary<uint, Brush> _brushes = new();
    private readonly Random _random = new(7);

    private double _time;
    private double _sideAngle, _sideVelocity, _depthAngle, _depthVelocity, _squashAmount, _shake, _flash;
    private readonly double[] _glovePhase = { -1, -1 };
    private readonly int[] _gloveKind = new int[2];
    private int _nextGlove;
    private int _environment = -1;
    private Color _particleA = Colors.White, _particleB = Colors.LightGray, _markColor = Colors.Transparent, _outfitA = Colors.Blue, _outfitB = Colors.White;
    private string _marks = "marks_none", _decor = "decor_posters";

    private struct Particle { public double X, Y, Vx, Vy, Life, MaxLife, Size; public bool Alt; }
    private readonly Particle[] _particles = new Particle[MaxParticles];
    private int _nextParticle;

    private struct Mark { public double X, Y, Life; public int Kind; }
    private readonly Mark[] _hitMarks = new Mark[14];
    private int _nextMark;

    public PunchScene()
    {
        _viewport.Camera = _camera;
        _viewport.Children.Add(new ModelVisual3D { Content = _world });
        _world.Children.Add(_lights);
        _world.Children.Add(_room);

        // Chain + bag hang from a hinge; swinging rotates around it, squashing scales around the bag's middle.
        var bagGroup = new Model3DGroup();
        var chain = new MeshBuilder();
        chain.Box(new Point3D(-0.03, BagTop, -0.03), new Point3D(0.03, Hinge, 0.03));
        bagGroup.Children.Add(new GeometryModel3D(chain.Frozen(), new DiffuseMaterial(Brush(Color.FromRgb(0x9A, 0x9A, 0xA4)))));
        var bagMesh = new MeshBuilder();
        bagMesh.Cylinder(new Point3D(0, BagBottom, 0), BagRadius, BagTop - BagBottom, 22);
        _bag.Geometry = bagMesh.Frozen();
        bagGroup.Children.Add(_bag);
        _bagTransform.Children.Add(_squash);
        _bagTransform.Children.Add(new RotateTransform3D(_swingSide, 0, Hinge, 0));
        _bagTransform.Children.Add(new RotateTransform3D(_swingDepth, 0, Hinge, 0));
        bagGroup.Transform = _bagTransform;
        _world.Children.Add(bagGroup);

        var shadowMesh = new MeshBuilder();
        shadowMesh.Quad(new Point3D(-0.9, 0.01, 0.9), new Point3D(0.9, 0.01, 0.9), new Point3D(0.9, 0.01, -0.9), new Point3D(-0.9, 0.01, -0.9));
        _shadow.Geometry = shadowMesh.Frozen();
        var shadowBrush = new RadialGradientBrush(Color.FromArgb(150, 0, 0, 0), Color.FromArgb(0, 0, 0, 0));
        shadowBrush.Freeze();
        _shadow.Material = new DiffuseMaterial(shadowBrush);
        _shadow.Transform = _shadowPosition;
        _world.Children.Add(_shadow);

        for (var i = 0; i < 2; i++)
        {
            var glove = new MeshBuilder();
            glove.Sphere(new Point3D(0, 0, 0), 0.2, 0.17, 0.22, 10, 7);
            glove.Sphere(new Point3D(0.17, 0.02, -0.02), 0.07, 0.09, 0.08, 6, 4); // thumb
            glove.Cylinder(new Point3D(0, -0.02, 0.16), 0.12, 0.05, 10);           // white cuff seam
            glove.Sphere(new Point3D(0, -0.01, 0.26), 0.13, 0.12, 0.14, 8, 5);   // cuff
            _gloves[i].Geometry = glove.Frozen();
            var group = new Transform3DGroup();
            group.Children.Add(_gloveScales[i]);
            group.Children.Add(_glovePositions[i]);
            _gloves[i].Transform = group;
            _world.Children.Add(_gloves[i]);
        }
        _lights.Children.Add(_furyLight);
        // The whole scene (3D + sprites) is cached by the GPU at 384×216 and stretched without smoothing:
        // a parent scale (Viewbox) only enlarges the cached bitmap, so every frame stays pixelated and fast.
        _sprites = new SpriteLayer(this) { Width = W, Height = H, IsHitTestVisible = false };
        _root.Children.Add(_viewport);
        _root.Children.Add(_sprites);
        _root.CacheMode = new BitmapCache { RenderAtScale = 1, EnableClearType = false, SnapsToDevicePixels = false };
        RenderOptions.SetBitmapScalingMode(_root, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_sprites, EdgeMode.Aliased);
        _root.Measure(new Size(W, H));
        _root.Arrange(new Rect(0, 0, W, H));
        SetCosmetics(PunchContent.Cosmetic("glove_red"), PunchContent.Cosmetic("bag_red"), PunchContent.Cosmetic("particles_white"), PunchContent.Cosmetic("marks_none"),
            PunchContent.Cosmetic("outfit_track"), PunchContent.Cosmetic("decor_posters"));
        SetEnvironment(0);
    }

    /// <summary>The 384×216 scene element; put it into a Viewbox to show it large.</summary>
    public FrameworkElement View => _root;
    public bool ReducedMotion { get; set; }
    public bool ScreenShake { get; set; } = true;
    public bool Flashes { get; set; } = true;
    public double LastRenderMs { get; private set; }

    // ───────────────────────── looks ─────────────────────────

    public void SetCosmetics(CosmeticDef glove, CosmeticDef bag, CosmeticDef particles, CosmeticDef marks, CosmeticDef outfit, CosmeticDef decor)
    {
        var gloveMaterial = new MaterialGroup();
        gloveMaterial.Children.Add(new DiffuseMaterial(Brush(glove.Primary)));
        gloveMaterial.Children.Add(new SpecularMaterial(Brush(Color.FromArgb(180, 255, 255, 255)), glove.Id is "glove_chrome" or "glove_gold" ? 60 : 25));
        gloveMaterial.Freeze();
        foreach (var model in _gloves) model.Material = gloveMaterial;

        var bagMaterial = new MaterialGroup();
        bagMaterial.Children.Add(new DiffuseMaterial(Texture(64, 40, (x, y) => BagPixel(x, y, bag))));
        bagMaterial.Children.Add(new SpecularMaterial(Brush(Color.FromArgb(110, 255, 255, 255)), bag.Id == "bag_holo" ? 80 : 30));
        bagMaterial.Children.Add(new EmissiveMaterial(_furyGlow));
        _bag.Material = bagMaterial;
        _bag.BackMaterial = bagMaterial;
        (_particleA, _particleB) = (particles.Primary, particles.Secondary);
        _marks = marks.Id;
        _markColor = marks.Primary;
        (_outfitA, _outfitB) = (outfit.Primary, outfit.Secondary);
        _decor = decor.Id;
    }

    private static Color BagPixel(int x, int y, CosmeticDef bag)
    {
        var a = bag.Primary;
        var b = bag.Secondary;
        if (bag.Id == "bag_camo") return ((x / 5 + y / 4 + (x * 7 + y * 3) % 5) % 3) switch { 0 => a, 1 => b, _ => Shade(a, 0.75) };
        if (bag.Id == "bag_bw") return x % 32 < 16 ? a : b;
        if (bag.Id == "bag_holo") return Mix(a, b, (Math.Sin(x * 0.35 + y * 0.2) + 1) / 2);
        if (y < 3 || y > 36) return b; // top and bottom bands
        if (y is 12 or 27) return Shade(b, 1.2); // stitched seams
        if (x is >= 26 and <= 37 && y is >= 16 and <= 23)
        {
            // Pixel "PF" logo on the front.
            string[] logo = { "111001110", "100101000", "111001100", "100001000", "100001000" };
            var lx = (x - 27) % 12;
            var ly = (y - 17);
            if (ly is >= 0 and < 5 && lx is >= 0 and < 9 && logo[ly][lx] == '1') return Color.FromRgb(0xF2, 0xE8, 0xD8);
        }
        var noise = ((x * 17 + y * 31) % 7) / 60.0;
        return Shade(a, 0.92 + noise);
    }

    /// <summary>Builds the room for one of the eight studios: textures, lights and the backdrop colour.</summary>
    public void SetEnvironment(int index)
    {
        index = Math.Clamp(index, 0, 7);
        if (index == _environment) return;
        _environment = index;
        _room.Children.Clear();
        _lights.Children.Clear();
        _lights.Children.Add(_furyLight);
        var floor = new MeshBuilder();
        floor.Quad(new Point3D(-7, 0, 3.5), new Point3D(7, 0, 3.5), new Point3D(7, 0, -4), new Point3D(-7, 0, -4));
        var back = new MeshBuilder();
        back.Quad(new Point3D(-7, 0, -4), new Point3D(7, 0, -4), new Point3D(7, 5.5, -4), new Point3D(-7, 5.5, -4));
        var left = new MeshBuilder();
        left.Quad(new Point3D(-6, 0, 3.5), new Point3D(-6, 0, -4), new Point3D(-6, 5.5, -4), new Point3D(-6, 5.5, 3.5));
        var right = new MeshBuilder();
        right.Quad(new Point3D(6, 0, -4), new Point3D(6, 0, 3.5), new Point3D(6, 5.5, 3.5), new Point3D(6, 5.5, -4));
        var beam = new MeshBuilder();
        beam.Box(new Point3D(-4, Hinge, -0.15), new Point3D(4, Hinge + 0.22, 0.15));

        var (floorTex, wallTex, sideTex) = Textures(index);
        var floorMaterial = new MaterialGroup();
        floorMaterial.Children.Add(new DiffuseMaterial(floorTex));
        if (index >= 3) floorMaterial.Children.Add(new SpecularMaterial(Brush(Color.FromArgb(90, 255, 255, 255)), 40)); // glossy arena floors
        _room.Children.Add(new GeometryModel3D(floor.Frozen(), floorMaterial));
        _room.Children.Add(new GeometryModel3D(back.Frozen(), new DiffuseMaterial(wallTex)));
        _room.Children.Add(new GeometryModel3D(left.Frozen(), new DiffuseMaterial(sideTex)));
        _room.Children.Add(new GeometryModel3D(right.Frozen(), new DiffuseMaterial(sideTex)));
        _room.Children.Add(new GeometryModel3D(beam.Frozen(), new DiffuseMaterial(Brush(index >= 5 ? Color.FromRgb(0x50, 0x58, 0x68) : Color.FromRgb(0x3A, 0x2E, 0x26)))));

        var (ambient, key, keyDir, lamp, lampPos) = Lighting(index);
        _lights.Children.Add(new AmbientLight(ambient));
        _lights.Children.Add(new DirectionalLight(key, keyDir));
        _lights.Children.Add(new PointLight(lamp, lampPos) { Range = 9, ConstantAttenuation = 0.6, LinearAttenuation = 0.12 });
        if (index >= 3)
        {
            // Arena spotlights from above.
            _lights.Children.Add(new SpotLight(Color.FromRgb(0xFF, 0xF2, 0xD8), new Point3D(-2.5, 5, 2), new Vector3D(0.5, -1, -0.4), 40, 20));
            _lights.Children.Add(new SpotLight(Color.FromRgb(0xD8, 0xE8, 0xFF), new Point3D(2.5, 5, 2), new Vector3D(-0.5, -1, -0.4), 40, 20));
        }
    }

    private (Color Ambient, Color Key, Vector3D KeyDirection, Color Lamp, Point3D LampPosition) Lighting(int index) => index switch
    {
        0 => (Color.FromRgb(0x3A, 0x32, 0x2C), Color.FromRgb(0x8A, 0x78, 0x66), new Vector3D(-0.3, -1, -0.6), Color.FromRgb(0xFF, 0xC8, 0x7A), new Point3D(0.8, 3.4, 1.4)),
        1 => (Color.FromRgb(0x44, 0x44, 0x4E), Color.FromRgb(0xA8, 0xA4, 0x9A), new Vector3D(-0.4, -1, -0.5), Color.FromRgb(0xFF, 0xE6, 0xC0), new Point3D(-1, 3.6, 1.6)),
        2 => (Color.FromRgb(0x50, 0x54, 0x5C), Color.FromRgb(0xC8, 0xCC, 0xD4), new Vector3D(0.2, -1, -0.6), Color.FromRgb(0xE8, 0xF2, 0xFF), new Point3D(1, 3.8, 1.8)),
        3 => (Color.FromRgb(0x2A, 0x2E, 0x3A), Color.FromRgb(0x70, 0x78, 0x8A), new Vector3D(0, -1, -0.4), Color.FromRgb(0xFF, 0xE0, 0xA0), new Point3D(0, 4.2, 2)),
        4 => (Color.FromRgb(0x30, 0x24, 0x2A), Color.FromRgb(0x88, 0x70, 0x70), new Vector3D(0, -1, -0.3), Color.FromRgb(0xFF, 0xD0, 0x60), new Point3D(0, 4.4, 2.2)),
        5 => (Color.FromRgb(0x1A, 0x2A, 0x36), Color.FromRgb(0x50, 0x80, 0x98), new Vector3D(-0.3, -1, -0.5), Color.FromRgb(0x6A, 0xF2, 0xFF), new Point3D(-1.2, 3.4, 1.4)),
        6 => (Color.FromRgb(0x2A, 0x16, 0x34), Color.FromRgb(0x80, 0x50, 0x90), new Vector3D(0.3, -1, -0.5), Color.FromRgb(0xFF, 0x5A, 0xD0), new Point3D(1.4, 3.6, 1.6)),
        _ => (Color.FromRgb(0x16, 0x1A, 0x2A), Color.FromRgb(0x60, 0x70, 0xA0), new Vector3D(-0.6, -0.8, -0.4), Color.FromRgb(0x9A, 0xC8, 0xFF), new Point3D(0, 3.8, 1.8))
    };

    private (Brush Floor, Brush Wall, Brush Side) Textures(int index)
    {
        switch (index)
        {
            case 0: // old garage: concrete, bricks, roller door
                return (Texture(96, 48, (x, y) => Shade(Color.FromRgb(0x6A, 0x66, 0x60), 0.85 + Hash(x, y) * 0.25 - (Math.Pow(x - 30, 2) + Math.Pow(y - 30, 2) < 60 ? 0.3 : 0))),
                    Texture(128, 48, (x, y) => x is > 44 and < 84 && y > 8
                        ? (y % 4 == 0 ? Color.FromRgb(0x5A, 0x5E, 0x66) : Color.FromRgb(0x7A, 0x80, 0x8A))
                        : Brick(x, y, Color.FromRgb(0x8E, 0x44, 0x36))),
                    Texture(48, 48, (x, y) => Brick(x, y, Color.FromRgb(0x84, 0x40, 0x34))));
            case 1: // small gym: wood floor, blue walls with stripe
                return (Texture(96, 48, (x, y) => Shade(Color.FromRgb(0xA8, 0x74, 0x44), (y % 6 == 0 ? 0.7 : 0.95) + Hash(x / 8, y / 6) * 0.15)),
                    Texture(128, 48, (x, y) => y is 20 or 21 ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Poster(x, y) ?? Shade(Color.FromRgb(0x3A, 0x6A, 0xB0), 0.95 + Hash(x, y) * 0.08)),
                    Texture(48, 48, (x, y) => y is 20 or 21 ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x34, 0x60, 0xA0)));
            case 2: // fitness studio: rubber tiles, mirrors
                return (Texture(96, 48, (x, y) => Shade(Color.FromRgb(0x2E, 0x30, 0x36), ((x / 12 + y / 12) % 2 == 0 ? 1 : 0.85) + Hash(x, y) * 0.1)),
                    Texture(128, 48, (x, y) => y > 10 && y < 40 && x % 32 > 2 ? Mix(Color.FromRgb(0xB8, 0xC8, 0xD8), Colors.White, (x + y) % 23 == 0 ? 0.6 : 0.1) : Color.FromRgb(0xD8, 0xD8, 0xDC)),
                    Texture(48, 48, (x, y) => Color.FromRgb(0xC8, 0xCA, 0xD0)));
            case 3: // local arena: blue canvas, crowd
                return (Texture(96, 48, (x, y) => y < 3 ? Colors.White : Shade(Color.FromRgb(0x2A, 0x5A, 0xB8), 0.9 + Hash(x, y) * 0.1)),
                    Texture(128, 48, (x, y) => Crowd(x, y, Color.FromRgb(0x1A, 0x1C, 0x26), 0.35)),
                    Texture(48, 48, (x, y) => Crowd(x, y, Color.FromRgb(0x16, 0x18, 0x22), 0.3)));
            case 4: // world championship arena: red canvas, gold banners
                return (Texture(96, 48, (x, y) => Math.Abs(x - 48) < 10 && Math.Abs(y - 24) < 6 ? Color.FromRgb(0xF2, 0xC2, 0x32) : Shade(Color.FromRgb(0xB8, 0x26, 0x2E), 0.9 + Hash(x, y) * 0.1)),
                    Texture(128, 48, (x, y) => y < 8 && x % 20 < 14 ? Color.FromRgb(0xE8, 0xB8, 0x30) : Crowd(x, y, Color.FromRgb(0x20, 0x14, 0x18), 0.5)),
                    Texture(48, 48, (x, y) => Crowd(x, y, Color.FromRgb(0x1C, 0x12, 0x16), 0.45)));
            case 5: // high-tech lab: metal grid, neon lines
                return (Texture(96, 48, (x, y) => x % 12 == 0 || y % 12 == 0 ? Color.FromRgb(0x2A, 0xC8, 0xE8) : Shade(Color.FromRgb(0x22, 0x28, 0x30), 0.9 + Hash(x, y) * 0.1)),
                    Texture(128, 48, (x, y) => y % 16 == 0 || (x % 32 == 0 && y > 6) ? Color.FromRgb(0x3A, 0xE8, 0xFF) : Shade(Color.FromRgb(0x1A, 0x22, 0x2C), 0.9 + Hash(x / 4, y / 4) * 0.2)),
                    Texture(48, 48, (x, y) => y % 16 == 0 ? Color.FromRgb(0x3A, 0xE8, 0xFF) : Color.FromRgb(0x18, 0x1E, 0x28)));
            case 6: // futuristic mega arena: glossy black, magenta LED wall
                return (Texture(96, 48, (x, y) => (x + y) % 16 == 0 ? Color.FromRgb(0xFF, 0x3A, 0xC8) : Color.FromRgb(0x10, 0x0C, 0x16)),
                    Texture(128, 48, (x, y) => Mix(Color.FromRgb(0x6A, 0x1A, 0xC8), Color.FromRgb(0xFF, 0x3A, 0x9A), (Math.Sin(x * 0.12) + 1) / 2 * (y % 3 == 0 ? 0.6 : 1))),
                    Texture(48, 48, (x, y) => y % 6 == 0 ? Color.FromRgb(0xFF, 0x3A, 0xC8) : Color.FromRgb(0x18, 0x10, 0x22)));
            default: // orbital station: hex floor, stars and a planet
                return (Texture(96, 48, (x, y) => ((x + (y / 6 % 2) * 6) % 12 == 0 || y % 6 == 0) ? Color.FromRgb(0x5A, 0x66, 0x7A) : Color.FromRgb(0x2E, 0x34, 0x42)),
                    Texture(128, 48, (x, y) =>
                    {
                        var dx = x - 96;
                        var dy = y - 70;
                        if (dx * dx + dy * dy < 50 * 50) return Mix(Color.FromRgb(0x2A, 0x6A, 0xE8), Color.FromRgb(0x3A, 0xC8, 0x8A), Hash(x / 3, y / 3) > 0.7 ? 0.8 : 0.1);
                        return Hash(x, y) > 0.97 ? Colors.White : Color.FromRgb(0x06, 0x08, 0x14);
                    }),
                    Texture(48, 48, (x, y) => x % 16 < 2 ? Color.FromRgb(0x6A, 0x74, 0x88) : Color.FromRgb(0x2A, 0x30, 0x3C)));
        }
    }

    private static Color Brick(int x, int y, Color color)
    {
        var row = y / 4;
        var offset = row % 2 == 0 ? 0 : 4;
        if (y % 4 == 3 || (x + offset) % 8 == 7) return Color.FromRgb(0x9A, 0x92, 0x88); // mortar
        return Shade(color, 0.85 + Hash((x + offset) / 8, row) * 0.3);
    }

    private static Color? Poster(int x, int y) =>
        x is > 10 and < 26 && y is > 4 and < 18 ? (y < 8 ? Color.FromRgb(0xC8, 0x3A, 0x3A) : Color.FromRgb(0xE8, 0xD8, 0xB0)) : null;

    private static Color Crowd(int x, int y, Color back, double density)
    {
        if (y < 6) return back;
        var head = Hash(x / 2, y / 3);
        if (head < density && (y % 3) != 0) return ((int)(head * 100) % 4) switch { 0 => Color.FromRgb(0xE8, 0xC8, 0xA8), 1 => Color.FromRgb(0x8A, 0x5A, 0x3A), 2 => Color.FromRgb(0xC8, 0x3A, 0x3A), _ => Color.FromRgb(0x3A, 0x6A, 0xC8) };
        return Shade(back, 0.8 + (y / 6 % 2) * 0.3);
    }

    // ───────────────────────── actions ─────────────────────────

    /// <summary>A visible punch: a glove flies in, the bag swings and squashes, pixels fly, maybe a mark stays.</summary>
    public void Hit(PunchResult punch)
    {
        var side = _nextGlove;
        _nextGlove = 1 - _nextGlove;
        _glovePhase[side] = 0;
        _gloveKind[side] = _random.Next(3); // jab, hook, uppercut
        var strength = Math.Min(4, punch.Strength);
        var direction = side == 0 ? 1 : -1;
        _sideVelocity += direction * (6 + 4 * strength) * (_gloveKind[side] == 1 ? 1.6 : 1);
        _depthVelocity -= (8 + 5 * strength) * (_gloveKind[side] == 0 ? 1.4 : 0.6);
        _squashAmount = Math.Min(0.35, _squashAmount + 0.08 * strength);
        if (!ReducedMotion && ScreenShake) _shake = Math.Max(_shake, punch.Critical || punch.Fury ? 0.3 : strength > 1.5 ? 0.15 : 0);
        if (punch.Critical && Flashes && !ReducedMotion) _flash = 0.18;

        var hit = Project(new Point3D(direction * -0.25, 2.3 + (_gloveKind[side] == 2 ? -0.35 : 0.1), BagRadius));
        var count = (punch.Fury ? 26 : 10) + (punch.Critical ? 18 : 0);
        for (var i = 0; i < count; i++)
        {
            ref var p = ref _particles[_nextParticle];
            _nextParticle = (_nextParticle + 1) % MaxParticles;
            var angle = _random.NextDouble() * Math.PI * 2;
            var speed = 40 + _random.NextDouble() * (punch.Fury ? 160 : 100);
            p = new Particle
            {
                X = hit.X, Y = hit.Y, Vx = Math.Cos(angle) * speed - direction * 30, Vy = Math.Sin(angle) * speed - 40,
                Life = 0.35 + _random.NextDouble() * 0.4, Size = punch.Critical || punch.Fury ? 2 + _random.Next(2) : 1 + _random.Next(2), Alt = _random.Next(2) == 0
            };
            p.MaxLife = p.Life;
        }
        if (_marks != "marks_none")
        {
            ref var mark = ref _hitMarks[_nextMark];
            _nextMark = (_nextMark + 1) % _hitMarks.Length;
            mark = new Mark { X = direction * -(4 + _random.NextDouble() * 10), Y = -10 + _random.NextDouble() * 24, Life = 3, Kind = _marks == "marks_stars" ? 1 : _marks == "marks_bolts" ? 2 : 0 };
        }
    }

    // ───────────────────────── frame ─────────────────────────

    public void Advance(double seconds, bool fury)
    {
        _time += seconds;
        // Damped springs for the swing (radians in degrees for the transforms).
        _sideVelocity += (-_sideAngle * 28 - _sideVelocity * 3.2) * seconds;
        _sideAngle += _sideVelocity * seconds;
        _depthVelocity += (-_depthAngle * 26 - _depthVelocity * 3.0) * seconds;
        _depthAngle += _depthVelocity * seconds;
        _sideAngle = Math.Clamp(_sideAngle, -24, 24);
        _depthAngle = Math.Clamp(_depthAngle, -24, 24);
        if (fury && !ReducedMotion) _sideAngle += Math.Sin(_time * 22) * 0.4;
        _squashAmount = Math.Max(0, _squashAmount - seconds * 1.6);
        _shake = Math.Max(0, _shake - seconds);
        _flash = Math.Max(0, _flash - seconds);
        for (var i = 0; i < 2; i++) if (_glovePhase[i] >= 0) _glovePhase[i] = _glovePhase[i] + seconds / 0.22 > 1 ? -1 : _glovePhase[i] + seconds / 0.22;
        for (var i = 0; i < MaxParticles; i++)
        {
            ref var p = ref _particles[i];
            if (p.Life <= 0) continue;
            p.Life -= seconds;
            p.Vy += 260 * seconds;
            p.X += p.Vx * seconds;
            p.Y += p.Vy * seconds;
        }
        for (var i = 0; i < _hitMarks.Length; i++) _hitMarks[i].Life -= seconds;
        _furyGlow.Color = fury ? Color.FromArgb((byte)(70 + 50 * Math.Sin(_time * 10)), 255, 110, 30) : Color.FromArgb(0, 0, 0, 0);
        _furyLight.Color = fury ? Color.FromRgb((byte)(160 + 60 * Math.Sin(_time * 10)), 60, 20) : Colors.Black;
    }

    /// <summary>Renders the 3D room and the pixel sprites into the low-resolution frame.</summary>
    public void Render(SceneState state)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var shakeX = _shake > 0 ? Math.Sin(_time * 80) * _shake * 0.25 : 0;
        var shakeY = _shake > 0 ? Math.Cos(_time * 67) * _shake * 0.15 : 0;
        _camera.Position = new Point3D(shakeX, 2.45 + shakeY, 6.3);
        _camera.LookDirection = new Vector3D(0, -0.07, -1);
        _swingSide.Angle = _sideAngle;
        _swingDepth.Angle = _depthAngle;
        _squash.ScaleX = _squash.ScaleZ = 1 + _squashAmount * 0.6;
        _squash.ScaleY = 1 - _squashAmount * 0.35;
        _shadowPosition.OffsetX = Math.Sin(_sideAngle * Math.PI / 180) * -1.6;
        _shadowPosition.OffsetZ = Math.Sin(_depthAngle * Math.PI / 180) * 1.6;
        for (var i = 0; i < 2; i++) PlaceGlove(i);

        _state = state;
        _sprites.InvalidateVisual();
        LastRenderMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void PlaceGlove(int i)
    {
        var phase = _glovePhase[i];
        var side = i == 0 ? -1 : 1;
        if (phase < 0)
        {
            _gloveScales[i].ScaleX = _gloveScales[i].ScaleY = _gloveScales[i].ScaleZ = 0.0001;
            return;
        }
        // Out to the bag in the first 40 %, back afterwards.
        var t = phase < 0.4 ? phase / 0.4 : 1 - (phase - 0.4) / 0.6;
        t = 1 - Math.Pow(1 - t, 3);
        var start = _gloveKind[i] switch { 1 => new Point3D(side * 1.7, 2.1, 1.6), 2 => new Point3D(side * 0.7, 1.0, 1.8), _ => new Point3D(side * 0.8, 2.2, 2.4) };
        var impact = new Point3D(side * 0.3, _gloveKind[i] == 2 ? 1.95 : 2.35, BagRadius + 0.16);
        _glovePositions[i].OffsetX = start.X + (impact.X - start.X) * t;
        _glovePositions[i].OffsetY = start.Y + (impact.Y - start.Y) * t;
        _glovePositions[i].OffsetZ = start.Z + (impact.Z - start.Z) * t;
        _gloveScales[i].ScaleX = side; // mirrored left glove
        _gloveScales[i].ScaleY = _gloveScales[i].ScaleZ = 1;
    }

    /// <summary>World point → pixel in the 384×216 frame (same camera as the viewport).</summary>
    public Point Project(Point3D world)
    {
        var position = _camera.Position;
        var forward = _camera.LookDirection;
        forward.Normalize();
        var right = Vector3D.CrossProduct(forward, _camera.UpDirection);
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        var d = world - position;
        var z = Vector3D.DotProduct(d, forward);
        if (z <= 0.01) return new Point(-100, -100);
        var scale = W / 2.0 / Math.Tan(_camera.FieldOfView * Math.PI / 360);
        return new Point(W / 2.0 + Vector3D.DotProduct(d, right) / z * scale, H / 2.0 - Vector3D.DotProduct(d, up) / z * scale);
    }

    /// <summary>Generous hit area around the swinging bag.</summary>
    public bool HitsBag(Point frame)
    {
        var swing = Math.Sin(_sideAngle * Math.PI / 180) * -(Hinge - 2.2);
        var top = Project(new Point3D(swing * 0.4, BagTop, 0));
        var bottom = Project(new Point3D(swing, BagBottom, 0));
        var half = Project(new Point3D(BagRadius + 0.25, 2.2, 0)).X - Project(new Point3D(0, 2.2, 0)).X;
        var minX = Math.Min(top.X, bottom.X) - half;
        var maxX = Math.Max(top.X, bottom.X) + half;
        return frame.X >= minX && frame.X <= maxX && frame.Y >= top.Y - 6 && frame.Y <= bottom.Y + 10;
    }

    public static Point EventPosition(ActiveEvent active, double time) => new(active.X * W, active.Y * H + Math.Sin(time * 3 + active.X * 10) * 4);

    public bool HitsEvent(Point frame, ActiveEvent active)
    {
        var center = EventPosition(active, _time);
        return Math.Abs(frame.X - center.X) < 16 && Math.Abs(frame.Y - center.Y) < 16;
    }

    // ───────────────────────── pixel sprites ─────────────────────────

    private void Px(DrawingContext dc, Color color, double x, double y, double w = 1, double h = 1) => dc.DrawRectangle(Brush(color), null, new Rect(Math.Round(x), Math.Round(y), w, h));

    internal void DrawSprites(DrawingContext dc) => DrawOverlay(dc, _state);

    /// <summary>A picture of the current frame at 384×216 (software rendering – for tests and thumbnails).</summary>
    public BitmapSource Snapshot()
    {
        _root.Measure(new Size(W, H));
        _root.Arrange(new Rect(0, 0, W, H));
        _root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_root);
        bitmap.Freeze();
        return bitmap;
    }

    private void DrawOverlay(DrawingContext dc, SceneState state)
    {
        DrawDecor(dc);
        // Hit marks on the bag.
        var center = Project(new Point3D(-Math.Sin(_sideAngle * Math.PI / 180) * 0.9, 2.25, BagRadius));
        foreach (var mark in _hitMarks)
        {
            if (mark.Life <= 0) continue;
            dc.PushOpacity(Math.Min(1, mark.Life));
            var x = center.X + mark.X;
            var y = center.Y + mark.Y;
            switch (mark.Kind)
            {
                case 1: Px(dc, _markColor, x - 1, y, 3, 1); Px(dc, _markColor, x, y - 1, 1, 3); break;
                case 2: Px(dc, _markColor, x, y - 2, 1, 2); Px(dc, _markColor, x + 1, y, 1, 2); Px(dc, _markColor, x, y + 2, 1, 1); break;
                default: Px(dc, _markColor, x - 1, y - 1, 3, 2); break;
            }
            dc.Pop();
        }
        DrawProducers(dc, state);
        if (state.Event is { } active) DrawEvent(dc, active);
        foreach (var p in _particles)
        {
            if (p.Life <= 0) continue;
            var color = p.Alt ? _particleA : _particleB;
            if (_particleA == Color.FromRgb(0xFF, 0x4F, 0xA0)) color = Rainbow(p.X * 0.05 + _time); // rainbow particles
            dc.PushOpacity(Math.Min(1, p.Life / p.MaxLife * 1.5));
            Px(dc, state.Fury ? Mix(color, Color.FromRgb(0xFF, 0x7A, 0x1A), 0.5) : color, p.X, p.Y, p.Size, p.Size);
            dc.Pop();
        }
        if (_flash > 0) dc.DrawRectangle(Brush(Color.FromArgb((byte)(_flash / 0.18 * 120), 255, 255, 255)), null, new Rect(0, 0, W, H));
    }

    private void DrawDecor(DrawingContext dc)
    {
        switch (_decor)
        {
            case "decor_trophies":
                foreach (var x in new[] { 24.0, 40, 336, 352 })
                {
                    Px(dc, Color.FromRgb(0xF2, 0xC2, 0x32), x, 70, 7, 5);
                    Px(dc, Color.FromRgb(0xC8, 0x96, 0x1A), x + 2, 75, 3, 3);
                    Px(dc, Color.FromRgb(0x5A, 0x3A, 0x1A), x - 1, 78, 9, 2);
                }
                Px(dc, Color.FromRgb(0x5A, 0x3A, 0x1A), 18, 80, 36, 2);
                Px(dc, Color.FromRgb(0x5A, 0x3A, 0x1A), 330, 80, 36, 2);
                break;
            case "decor_neon":
                var glow = Color.FromArgb((byte)(200 + 55 * Math.Sin(_time * 4)), 0xFF, 0x3A, 0xA0);
                foreach (var (x, y) in new[] { (28.0, 40.0), (44, 40) })
                {
                    Px(dc, glow, x, y, 12, 1); Px(dc, glow, x, y, 1, 16); Px(dc, glow, x, y + 8, 10, 1);
                }
                Px(dc, Color.FromRgb(0x3A, 0xE8, 0xFF), 26, 60, 34, 1);
                break;
            case "decor_plants":
                foreach (var x in new[] { 16.0, 356 })
                {
                    Px(dc, Color.FromRgb(0x7A, 0x4E, 0x2A), x, 150, 10, 8);
                    for (var i = 0; i < 6; i++) Px(dc, Color.FromRgb(0x3A, (byte)(0x8A + i * 8), 0x4A), x - 2 + i * 2, 138 + (i % 3) * 3, 2, 12 - (i % 3) * 3);
                }
                break;
            default:
                Px(dc, Color.FromRgb(0xE8, 0xD8, 0xB0), 26, 40, 16, 22);
                Px(dc, Color.FromRgb(0xC8, 0x3A, 0x3A), 26, 40, 16, 6);
                Px(dc, Color.FromRgb(0x3A, 0x2A, 0x2A), 31, 50, 6, 9);
                Px(dc, Color.FromRgb(0xE8, 0xD8, 0xB0), 342, 44, 16, 22);
                Px(dc, Color.FromRgb(0x2A, 0x5A, 0xC8), 342, 44, 16, 6);
                break;
        }
    }

    /// <summary>Little pixel boxers and machines around the bag; more units → faster and more lively.</summary>
    private void DrawProducers(DrawingContext dc, SceneState state)
    {
        var slots = new (double X, double Y)[] { (30, 196), (66, 200), (102, 204), (300, 204), (336, 200), (360, 194), (138, 206), (250, 206), (190, 22), (60, 112), (330, 30) };
        for (var i = 0; i < state.Counts.Length && i < slots.Length; i++)
        {
            var count = state.Counts[i];
            if (count <= 0) continue;
            var (x, y) = slots[i];
            var speed = 1 + Math.Log10(count + 1) * 2.2;
            var phase = (_time * speed + i * 0.37) % 1;
            var punch = phase < 0.25;
            switch (i)
            {
                case 0: Boxer(dc, x, y, punch, _outfitA, _outfitB, 0.9); break;
                case 1: Boxer(dc, x, y, punch, _outfitB, _outfitA, 1); Px(dc, Color.FromRgb(0xF2, 0xC2, 0x32), x + 8, y - 16, 3, 3); break;
                case 2:
                    Boxer(dc, x, y, false, Color.FromRgb(0x2A, 0x2A, 0x30), Colors.White, 1);
                    Px(dc, Color.FromRgb(0xE8, 0xE0, 0xC8), x + 6, y - 14 + (punch ? -1 : 0), 4, 5); // clipboard
                    break;
                case 3: // double-end ball on its cords
                    Px(dc, Color.FromRgb(0x8A, 0x8A, 0x92), x + 4, y - 40, 1, 14);
                    var bob = Math.Sin(_time * speed * 9) * 3;
                    Px(dc, Color.FromRgb(0xC8, 0x2A, 0x2A), x + 2 + bob, y - 27, 5, 5);
                    Px(dc, Color.FromRgb(0x8A, 0x8A, 0x92), x + 4, y - 22, 1, 12);
                    Boxer(dc, x - 8, y, punch, _outfitA, _outfitB, 0.8);
                    break;
                case 4: // punching machine
                    Px(dc, Color.FromRgb(0x5A, 0x5E, 0x6A), x - 8, y - 20, 14, 20);
                    Px(dc, Color.FromRgb(0xE8, 0x3A, 0x3A), x - 6, y - 17, 4, 2);
                    Px(dc, Color.FromRgb(0x9A, 0x9E, 0xAA), x - 8 - (punch ? 8 : 3), y - 12, punch ? 8 : 3, 3);
                    break;
                case 5: // robot
                    Px(dc, Color.FromRgb(0xB8, 0xC0, 0xCC), x - 5, y - 26, 10, 9);
                    Px(dc, Color.FromRgb(0x3A, 0xE8, 0xFF), x - 3, y - 23, 2, 2);
                    Px(dc, Color.FromRgb(0x3A, 0xE8, 0xFF), x + 1, y - 23, 2, 2);
                    Px(dc, Color.FromRgb(0x8A, 0x92, 0xA0), x - 6, y - 16, 12, 12);
                    Px(dc, Color.FromRgb(0x8A, 0x92, 0xA0), x - 10 - (punch ? 6 : 0), y - 14, 4, 3);
                    Px(dc, Color.FromRgb(0x6A, 0x72, 0x80), x - 5, y - 4, 3, 4);
                    Px(dc, Color.FromRgb(0x6A, 0x72, 0x80), x + 2, y - 4, 3, 4);
                    break;
                case 6: // team of three
                    for (var k = 0; k < 3; k++) Boxer(dc, x + k * 9 - 9, y - (k % 2) * 2, (phase + k * 0.3) % 1 < 0.25, _outfitA, _outfitB, 0.75);
                    break;
                case 7: // studio sign
                    Px(dc, Color.FromRgb(0x2A, 0x2A, 0x34), x - 14, y - 14, 30, 10);
                    for (var k = 0; k < 6; k++) Px(dc, k % 2 == (int)(_time * 2) % 2 ? Color.FromRgb(0xF2, 0xC2, 0x32) : Color.FromRgb(0x8A, 0x6A, 0x1A), x - 11 + k * 4, y - 10, 2, 2);
                    break;
                case 8: // cheering audience along the back wall
                    var wallBase = Project(new Point3D(0, 0, -4)).Y;
                    for (var k = 0; k < 54; k++)
                    {
                        var hop = Math.Max(0, Math.Sin(_time * (3 + speed) + k * 0.9)) * 2;
                        var cx = 6 + k * 7;
                        if (cx is > 150 and < 234) continue; // keep the bag free
                        Px(dc, (k % 3) switch { 0 => Color.FromRgb(0xE8, 0xC8, 0xA8), 1 => Color.FromRgb(0xC8, 0x3A, 0x3A), _ => Color.FromRgb(0x3A, 0x6A, 0xC8) }, cx, wallBase - 5 - hop, 3, 3);
                        Px(dc, Color.FromRgb(0x2A, 0x2A, 0x34), cx - 1, wallBase - 2 - hop, 5, 3);
                    }
                    break;
                case 9: // holograms
                    dc.PushOpacity(0.5 + 0.4 * Math.Sin(_time * 12));
                    Boxer(dc, x, y, punch, Color.FromRgb(0x3A, 0xE8, 0xFF), Color.FromRgb(0x9A, 0xF8, 0xFF), 1);
                    dc.Pop();
                    break;
                case 10: // orbital station crossing the top
                    var ox = (x + _time * 12) % (W + 40) - 20;
                    Px(dc, Color.FromRgb(0xC8, 0xD0, 0xDC), ox, y, 8, 4);
                    Px(dc, Color.FromRgb(0x2A, 0x6A, 0xE8), ox - 7, y + 1, 6, 2);
                    Px(dc, Color.FromRgb(0x2A, 0x6A, 0xE8), ox + 9, y + 1, 6, 2);
                    if (punch) Px(dc, Color.FromRgb(0xFF, 0x5A, 0x3A), ox + 3, y + 4, 2, 6);
                    break;
            }
        }
    }

    private void Boxer(DrawingContext dc, double x, double y, bool punch, Color shirt, Color trim, double scale)
    {
        var s = scale;
        Px(dc, Color.FromRgb(0xF2, 0xC1, 0x95), x - 2 * s, y - 22 * s, 4 * s + 1, 4 * s + 1); // head
        Px(dc, shirt, x - 3 * s, y - 17 * s, 6 * s + 1, 8 * s);                           // body
        Px(dc, trim, x - 3 * s, y - 11 * s, 6 * s + 1, 1);
        Px(dc, Color.FromRgb(0x2A, 0x2E, 0x3A), x - 3 * s, y - 9 * s, 2 * s + 1, 9 * s);   // legs
        Px(dc, Color.FromRgb(0x2A, 0x2E, 0x3A), x + 1 * s, y - 9 * s, 2 * s + 1, 9 * s);
        Px(dc, Color.FromRgb(0xD8, 0x31, 0x3A), x - (punch ? 9 : 6) * s, y - 16 * s, 3 * s + 1, 3 * s + 1); // gloves
        Px(dc, Color.FromRgb(0xD8, 0x31, 0x3A), x + 3 * s, y - 15 * s, 3 * s + 1, 3 * s + 1);
    }

    private void DrawEvent(DrawingContext dc, ActiveEvent active)
    {
        var p = EventPosition(active, _time);
        var (x, y) = (p.X, p.Y);
        var blink = active.Remaining < 2 && (int)(_time * 8) % 2 == 0;
        if (blink) return;
        // Soft glow so the target stands out in every studio.
        dc.DrawEllipse(Brush(Color.FromArgb(70, 255, 230, 120)), null, new Point(x, y), 14, 14);
        switch (active.Def.Id)
        {
            case "glove":
                Px(dc, Color.FromRgb(0xF2, 0xC2, 0x32), x - 5, y - 5, 10, 9);
                Px(dc, Color.FromRgb(0xC8, 0x96, 0x1A), x - 4, y + 4, 8, 3);
                Px(dc, Color.FromRgb(0xFF, 0xF4, 0xB0), x - 3, y - 4, 2, 2);
                if ((int)(_time * 6) % 2 == 0) Px(dc, Colors.White, x + 6, y - 8, 1, 3);
                break;
            case "drone":
                Px(dc, Color.FromRgb(0x5A, 0x60, 0x6E), x - 5, y - 2, 10, 4);
                var rotor = (int)(_time * 20) % 2 == 0 ? 6 : 3;
                Px(dc, Color.FromRgb(0xC8, 0xD0, 0xDC), x - 6 - rotor / 2.0, y - 5, rotor, 1);
                Px(dc, Color.FromRgb(0xC8, 0xD0, 0xDC), x + 5 - rotor / 2.0, y - 5, rotor, 1);
                Px(dc, Color.FromRgb(0xFF, 0x3A, 0x3A), x - 1, y + 2, 2, 1);
                break;
            case "challenger":
                Boxer(dc, x, y + 10, (int)(_time * 4) % 2 == 0, Color.FromRgb(0x2A, 0x2A, 0x2A), Color.FromRgb(0xE8, 0x3A, 0x3A), 1.1);
                Px(dc, Color.FromRgb(0xFF, 0x3A, 0x3A), x - 1, y - 22, 2, 5);
                Px(dc, Color.FromRgb(0xFF, 0x3A, 0x3A), x - 1, y - 15, 2, 2);
                for (var i = 0; i < active.HitsLeft && i < 12; i++) Px(dc, Color.FromRgb(0xFF, 0xD2, 0x4A), x - 12 + i * 2, y + 13, 1, 2);
                break;
            case "bonusbag":
                Px(dc, Color.FromRgb(0x9A, 0x9A, 0xA4), x, y - 9, 1, 3);
                Px(dc, Color.FromRgb(0x3A, 0xC8, 0x5A), x - 3, y - 6, 7, 12);
                Px(dc, Color.FromRgb(0xFF, 0xFF, 0xFF), x - 1, y - 2, 3, 3);
                break;
            case "motivation":
                for (var i = -4; i <= 4; i++) Px(dc, Color.FromRgb(0xFF, 0xD2, 0x4A), x + i, y - 4 + Math.Abs(i), 1, 9 - Math.Abs(i) * 2);
                break;
            default: // sponsor gift
                Px(dc, Color.FromRgb(0x3A, 0x6A, 0xE8), x - 5, y - 4, 10, 9);
                Px(dc, Color.FromRgb(0xF2, 0xC2, 0x32), x - 1, y - 4, 2, 9);
                Px(dc, Color.FromRgb(0xF2, 0xC2, 0x32), x - 5, y - 1, 10, 2);
                Px(dc, Color.FromRgb(0xF2, 0xC2, 0x32), x - 3, y - 7, 6, 2);
                break;
        }
        // Remaining time.
        var share = Math.Clamp(active.Remaining / active.Def.Lifetime, 0, 1);
        Px(dc, Color.FromArgb(150, 0, 0, 0), x - 10, y + 16, 20, 2);
        Px(dc, Color.FromRgb(0xFF, 0xE3, 0x6A), x - 10, y + 16, 20 * share, 2);
    }

    // ───────────────────────── helpers ─────────────────────────

    private Brush Brush(Color color)
    {
        var key = (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);
        if (_brushes.TryGetValue(key, out var brush)) return brush;
        if (_brushes.Count > 4096) _brushes.Clear(); // particles with fading colours never grow the cache forever
        brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushes[key] = brush;
        return brush;
    }

    /// <summary>
    /// Pixel texture. 3D materials are always filtered smoothly, so every texel is baked as a 4×4 block –
    /// the edges stay sharp after filtering.
    /// </summary>
    private static ImageBrush Texture(int width, int height, Func<int, int, Color> pixel)
    {
        const int block = 4;
        var bitmap = new WriteableBitmap(width * block, height * block, 96, 96, PixelFormats.Bgra32, null);
        var stride = width * block * 4;
        var data = new byte[stride * height * block];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var c = pixel(x, y);
                for (var by = 0; by < block; by++)
                    for (var bx = 0; bx < block; bx++)
                    {
                        var i = (y * block + by) * stride + (x * block + bx) * 4;
                        data[i] = c.B; data[i + 1] = c.G; data[i + 2] = c.R; data[i + 3] = c.A;
                    }
            }
        bitmap.WritePixels(new Int32Rect(0, 0, width * block, height * block), data, stride, 0);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor); // crisp texels
        brush.Freeze();
        return brush;
    }

    private static double Hash(int x, int y)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            return (h ^ (h >> 16)) / (double)uint.MaxValue;
        }
    }

    private static Color Shade(Color c, double f) => Color.FromArgb(c.A, (byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Color Rainbow(double t) => Color.FromRgb((byte)(128 + 127 * Math.Sin(t)), (byte)(128 + 127 * Math.Sin(t + 2.1)), (byte)(128 + 127 * Math.Sin(t + 4.2)));
}

/// <summary>Draws the pixel sprites of the current frame (inside the cached scene element).</summary>
internal sealed class SpriteLayer : FrameworkElement
{
    private readonly PunchScene _scene;

    public SpriteLayer(PunchScene scene) => _scene = scene;

    protected override void OnRender(DrawingContext dc) => _scene.DrawSprites(dc);
}

/// <summary>What the scene needs from the game each frame.</summary>
internal readonly record struct SceneState(int[] Counts, bool Fury, ActiveEvent? Event);
