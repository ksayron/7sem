using Burmalda.LibraryGame;
using Kebechet.Blazor.ThreeJS.Core;
using Kebechet.Blazor.ThreeJS.Math;
using Kebechet.Blazor.ThreeJS.Objects;
using V3 = System.Numerics.Vector3;

namespace Burmalda.Web.Services;

// All geometry, camera movement, collision checks, picking and animation are C#.
public sealed class LibraryScene(ThreeContext context)
{
    private readonly Scene scene = new();
    private readonly PerspectiveCamera camera = new(68, 1.6f, .08f, 80);
    private readonly Dictionary<string, Group> books = [];
    private readonly Dictionary<int, MeshStandardMaterial> shelfTrim = [];
    private readonly Dictionary<int, List<Object3D>> sectionObjects = [];
    private readonly Dictionary<int, Mesh> barriers = [];
    private readonly Dictionary<int, MeshStandardMaterial> materials = [];
    public V3 Position { get; private set; } = new(-6, 1.65f, 0);
    public float Yaw { get; private set; }
    public float Pitch { get; private set; } = -.22f;
    public string? TargetBook { get; private set; }
    public int? TargetShelf { get; private set; }
    public string TargetLabel { get; private set; } = "Подойдите к книге и наведите прицел";
    private float elapsed;

    private MeshStandardMaterial Material(int color)
    {
        if (materials.TryGetValue(color, out var material)) return material;
        material = new MeshStandardMaterial { Roughness = .85f };
        material.Color.SetHex(color); materials[color] = material; return material;
    }
    private Mesh Box(Object3D parent, float x, float y, float z, float w, float h, float d, int color)
    {
        var mesh = new Mesh(new BoxGeometry(w, h, d), Material(color));
        mesh.Position.Set(x, y, z); parent.Add(mesh); return mesh;
    }
    public async Task Build(GameState state)
    {
        context.Attach(scene);
        scene.Add(camera);
        scene.Add(new AmbientLight(Color.FromHex(0xE7D5AF), 1.4f));
        var sun = new DirectionalLight(Color.FromHex(0xFFE0A1), 2.4f); sun.Position.Set(-3, 8, 4); scene.Add(sun);
        var fill = new DirectionalLight(Color.FromHex(0x8099CC), .7f); fill.Position.Set(6, 3, -4); scene.Add(fill);
        Box(scene, 0, -.18f, 0, 20, .3f, 18, 0x79604A);
        // Broad floorboards and wall panels give the room a warm, authored scale.
        for (var i = 0; i < 20; i++) Box(scene, -9.5f + i, -.018f, 0, .965f, .025f, 18, i % 3 == 0 ? 0x917152 : 0x86684E);
        Box(scene, 0, 2.6f, -9, 20, 5.2f, .3f, 0xC1AA84);
        Box(scene, 0, 2.6f, 9, 20, 5.2f, .3f, 0xC1AA84);
        Box(scene, -10, 2.6f, 0, .3f, 5.2f, 18, 0xBAA17D);
        Box(scene, 10, 2.6f, 0, .3f, 5.2f, 18, 0xBAA17D);
        Box(scene, 0, 5.3f, 0, 20, .2f, 18, 0x8F7356);
        for (var i = -8; i <= 8; i += 4) {
            Box(scene, i, 4.85f, 0, .22f, .35f, 18, 0x493529);
            Box(scene, i, 1, -8.75f, .12f, 2, .15f, 0x72543B);
            Box(scene, i, 1, 8.75f, .12f, 2, .15f, 0x72543B);
        }
        // An inset arched-looking window with a simple timber grid.
        Box(scene, -9.78f, 3, -1, .12f, 2.6f, 3.1f, 0x577C8C);
        for (var i = -2; i <= 0; i++) Box(scene, -9.65f, 3, i, .12f, 2.7f, .08f, 0x3F3027);
        Box(scene, -9.65f, 3, -1, .12f, .08f, 3.2f, 0x3F3027);
        // Long central carpet leaves clear walking lanes around each pile.
        Box(scene, 0, .013f, 0, 17.5f, .018f, 2.1f, 0x41584F);
        Box(scene, 0, .025f, -.88f, 17, .008f, .035f, 0xC59C55);
        Box(scene, 0, .025f, .88f, 17, .008f, .035f, 0xC59C55);
        for (var s = 0; s < 6; s++) {
            var p = LibraryWorld.Shelf(s); var series = LibraryWorld.Series[s];
            var group = new Group(); group.Position.Set(p.X, 0, p.Z); scene.Add(group);
            sectionObjects[s] = [group];
            Box(group, 0, 1.9f, 0, 3.9f, 3.8f, .68f, 0x473427);
            Box(group, 0, 2, p.Z < 0 ? .38f : -.38f, 3.55f, 3.5f, .08f, 0x302C26);
            Box(group, -1.92f, 1.95f, 0, .16f, 3.9f, 1.0f, 0x654831);
            Box(group, 1.92f, 1.95f, 0, .16f, 3.9f, 1.0f, 0x654831);
            for (var row = 0; row < 4; row++) Box(group, 0, .2f + row * .92f, 0, 3.8f, .12f, 1.05f, 0x8B6544);
            Box(group, 0, 3.86f, 0, 4.1f, .18f, 1.12f, 0x8B6544);
            var trim = new MeshStandardMaterial { Roughness = .65f }; trim.Color.SetHex((int)series.Color); trim.Emissive.SetHex((int)series.Color);
            var nameplate = new Mesh(new BoxGeometry(3.5f, .27f, 1.14f), trim); nameplate.Position.Set(0, 3.58f, 0); group.Add(nameplate); shelfTrim[s] = trim;
            // Different geometric crests make categories readable without color alone.
            for (var crest = 0; crest <= s; crest++) Box(group, -.3f * s + crest * .6f, 3.58f, p.Z < 0 ? .6f : -.6f, .12f, .15f, .035f, 0xF0D9A3);
            // Modelled wall lamp with a warm emissive shade.
            var lampMat = new MeshStandardMaterial(); lampMat.Color.SetHex(0xF3C56B); lampMat.Emissive.SetHex(0xF3A73D); lampMat.EmissiveIntensity = .9f;
            var lamp = new Mesh(new CylinderGeometry(.18f, .32f, .45f, 10), lampMat); lamp.Position.Set(p.X, 4.45f, p.Z); scene.Add(lamp);
            Box(scene, p.X, 4.75f, p.Z, .06f, .3f, .06f, 0x493529);
            var barrier = Box(scene, p.X, 1.2f, p.Z < 0 ? -2.8f : 2.8f, 4.2f, 2.4f, .12f, 0x594B42); barriers[s] = barrier;
            for (var v = 1; v <= 12; v++) {
                var book = new Group();
                Box(book, 0, 0, 0, .38f, .17f, .56f, 0xD6C4A2);
                Box(book, 0, .1f, 0, .43f, .035f, .61f, (int)series.Color);
                Box(book, 0, -.1f, 0, .43f, .035f, .61f, (int)series.Color);
                Box(book, -.2f, 0, 0, .055f, .21f, .61f, (int)series.Color);
                Box(book, 0, .123f, 0, .16f, .012f, .25f, 0xC9A85F);
                // Volume ticks on covers are actual geometry, not CSS over the scene.
                for (var tick = 0; tick < (v - 1) % 4 + 1; tick++) Box(book, -.1f + tick * .065f, .132f, 0, .018f, .008f, .12f, 0x705335);
                scene.Add(book); books[LibraryWorld.BookId(s, v)] = book;
            }
        }
        Update(state, 0, false, false);
        await context.SetActiveSceneAsync(scene, camera);
    }
    public void Move(InputFrame input, float dt)
    {
        if (!input.Locked) return;
        Yaw -= input.Dx * .0022f; Pitch = Math.Clamp(Pitch - input.Dy * .0022f, -1.35f, 1.35f);
        if (input.Keys.Contains("ArrowLeft")) Yaw += dt * 1.5f;
        if (input.Keys.Contains("ArrowRight")) Yaw -= dt * 1.5f;
        if (input.Keys.Contains("ArrowUp")) Pitch = Math.Clamp(Pitch + dt, -1.35f, 1.35f);
        if (input.Keys.Contains("ArrowDown")) Pitch = Math.Clamp(Pitch - dt, -1.35f, 1.35f);
        var forward = new V3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw)); var right = new V3(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));
        var velocity = V3.Zero;
        if (input.Keys.Contains("KeyW")) velocity += forward;
        if (input.Keys.Contains("KeyS")) velocity -= forward;
        if (input.Keys.Contains("KeyD")) velocity += right;
        if (input.Keys.Contains("KeyA")) velocity -= right;
        if (velocity.LengthSquared() > 0) Position = LibraryWorld.Move(Position, V3.Normalize(velocity) * (3.8f * dt));
    }
    public void Update(GameState state, float dt, bool hint, bool reducedMotion)
    {
        elapsed += dt;
        camera.Position.Set(Position.X, Position.Y, Position.Z);
        camera.Rotation.Set(Pitch, Yaw, 0, EulerOrder.YXZ);
        TargetBook = null; TargetShelf = null;
        var direction = new V3(-MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), -MathF.Cos(Yaw) * MathF.Cos(Pitch));
        float best = 3.2f;
        foreach (var (id, book) in books) {
            var (series, volume) = GameRules.ParseBook(id);
            book.IsVisible = series < state.OpenSeries;
            if (!book.IsVisible) continue;
            var heldIndex = state.Held.IndexOf(id);
            V3 p;
            if (heldIndex >= 0) {
                var right = new V3(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));
                p = Position + direction * .75f + right * .36f + new V3(0, -.45f - heldIndex * .14f, 0);
                book.Rotation.Set(0, Yaw, -.12f, EulerOrder.XYZ);
            } else if (state.Shelved.Contains(id)) {
                var shelf = LibraryWorld.Shelf(series);
                p = shelf + new V3(-1.35f + (volume - 1) % 6 * .53f, .56f + (volume - 1) / 6 * .92f, series % 2 == 0 ? .35f : -.35f);
                book.Rotation.Set(0, 0, MathF.PI / 2, EulerOrder.XYZ);
            } else {
                p = LibraryWorld.BookPosition(series, volume);
                book.Rotation.Set(0, (volume % 3 - 1) * .18f, 0, EulerOrder.XYZ);
                var hit = LibraryWorld.RayBox(Position, direction, p, new(.33f, .25f, .4f));
                if (hit is { } distance && distance < best) { best = distance; TargetBook = id; }
            }
            book.Position.Set(p.X, p.Y, p.Z);
        }
        for (var s = 0; s < 6; s++) {
            var open = s < state.OpenSeries;
            barriers[s].IsVisible = !open;
            var selected = state.Held.LastOrDefault() is { } held && GameRules.ParseBook(held).Series == s;
            shelfTrim[s].EmissiveIntensity = hint && selected ? (reducedMotion ? .8f : .65f + MathF.Sin(elapsed * 3) * .25f) : .05f;
            if (!open) continue;
            var p = LibraryWorld.Shelf(s) + new V3(0, 1.7f, 0);
            var hit = LibraryWorld.RayBox(Position, direction, p, new(2, 1.9f, .7f));
            if (hit is { } distance && distance < best) { best = distance; TargetShelf = s; TargetBook = null; }
        }
        TargetLabel = TargetBook is { } target ? $"E · {LibraryWorld.Series[GameRules.ParseBook(target).Series].Name} · том {GameRules.ParseBook(target).Volume}" :
            TargetShelf is { } shelfId ? $"E · полка «{LibraryWorld.Series[shelfId].Name}»" : "Подойдите к книге или полке · взаимодействие E";
    }
    public Task Flush() => context.FlushAsync();
}
public sealed record InputFrame(string[] Keys, string[] Pressed, float Dx, float Dy, bool Click, bool Locked);

