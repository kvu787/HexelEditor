using Godot;
using Godot.Collections;

namespace GodotAgentDevExperiment;

/// <summary>Loads a glTF piece straight from a file on disk, with no import step, and gives every surface a
/// SimplePaint material whose parameters come from the custom properties Blender wrote on the material.</summary>
public static class PieceLoader
{
    public sealed record LoadedPiece(Node3D Root, int Surfaces, int PaintedSurfaces, List<string> Notes);

    public static LoadedPiece? Load(string path, Shader simplePaint, SessionLog log)
    {
        GltfDocument document = new();
        GltfState state = new();
        Error error = document.AppendFromFile(path, state);
        if (error != Error.Ok)
        {
            log.Write($"glTF load failed: {error} for {path}");
            return null;
        }
        Node? generated = document.GenerateScene(state);
        if (generated is not Node3D root)
        {
            log.Write("glTF scene root is not a Node3D.");
            return null;
        }

        int surfaces = 0;
        int painted = 0;
        List<string> notes = [];
        foreach (MeshInstance3D meshInstance in Descendants(root).OfType<MeshInstance3D>())
        {
            Mesh? mesh = meshInstance.Mesh;
            if (mesh is null)
            {
                continue;
            }
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                surfaces++;
                Material? imported = mesh.SurfaceGetMaterial(surface);
                Dictionary? extras = imported is not null && imported.HasMeta("extras") ? imported.GetMeta("extras").AsGodotDictionary() : null;
                ShaderMaterial paint = new() { Shader = simplePaint };
                if (extras is not null && extras.ContainsKey("SimplePaint.Brightness"))
                {
                    Godot.Collections.Array baseColor = extras["SimplePaint.BaseColorSrgb"].AsGodotArray();
                    paint.SetShaderParameter("BaseColorSrgb", new Color(baseColor[0].AsSingle(), baseColor[1].AsSingle(), baseColor[2].AsSingle()));
                    paint.SetShaderParameter("Brightness", extras["SimplePaint.Brightness"].AsSingle());
                    paint.SetShaderParameter("Shift", extras["SimplePaint.Shift"].AsSingle());
                    paint.SetShaderParameter("RotationDegrees", extras["SimplePaint.RotationDegrees"].AsSingle());
                    paint.SetShaderParameter("DarkPoint", extras["SimplePaint.DarkPoint"].AsSingle());
                    paint.SetShaderParameter("LightPoint", extras["SimplePaint.LightPoint"].AsSingle());
                    painted++;
                    notes.Add($"{meshInstance.Name} surface {surface}: SimplePaint from extras of material '{imported?.ResourceName}'");
                }
                else
                {
                    notes.Add($"{meshInstance.Name} surface {surface}: no SimplePaint extras on material '{imported?.ResourceName}'; defaults used");
                }
                meshInstance.SetSurfaceOverrideMaterial(surface, paint);
            }
        }
        return new LoadedPiece(root, surfaces, painted, notes);
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node grandchild in Descendants(child))
            {
                yield return grandchild;
            }
        }
    }
}
