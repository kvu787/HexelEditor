using Experiment.Core;
using Godot;

namespace GodotAgentDevExperiment;

/// <summary>The map's ground: one plane, one shader, one texture with a texel per cell. Painting writes the
/// texel and uploads the image. Godot's ImageTexture.Update replaces the whole texture, which is the known
/// cost on a large map; the checks measure it.</summary>
public partial class HexGround : MeshInstance3D
{
    public const float CellRadius = 1.0f;

    private Image _image = null!;
    private ImageTexture _texture = null!;
    private ShaderMaterial _material = null!;

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int Uploads { get; private set; }

    public void Build(int columns, int rows, Color fill)
    {
        Columns = columns;
        Rows = rows;
        _image = Image.CreateEmpty(columns, rows, false, Image.Format.Rgba8);
        _image.Fill(fill);
        _texture = ImageTexture.CreateFromImage(_image);

        (double width, double height) = GridExtent(columns, rows);
        PlaneMesh plane = new() { Size = new Vector2((float)width + 6.0f, (float)height + 6.0f) };
        Mesh = plane;
        // The plane is centered on the grid, whose cells start at the origin.
        Position = new Vector3((float)width / 2.0f - CellRadius, 0.0f, (float)height / 2.0f - CellRadius);

        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/HexGround.gdshader") };
        _material.SetShaderParameter("cells", _texture);
        _material.SetShaderParameter("grid_size", new Vector2(columns, rows));
        _material.SetShaderParameter("cell_radius", CellRadius);
        MaterialOverride = _material;
    }

    public static (double Width, double Height) GridExtent(int columns, int rows)
    {
        return (Math.Sqrt(3.0) * CellRadius * (columns + 0.5), 1.5 * CellRadius * rows + 0.5 * CellRadius);
    }

    public bool Contains(HexCell cell)
    {
        return cell.Column >= 0 && cell.Row >= 0 && cell.Column < Columns && cell.Row < Rows;
    }

    public Color CellColor(HexCell cell)
    {
        return _image.GetPixel(cell.Column, cell.Row);
    }

    /// <summary>Sets a cell's color and uploads the texture. Returns false for a cell off the grid.</summary>
    public bool Paint(HexCell cell, Color color)
    {
        if (!Contains(cell))
        {
            return false;
        }
        _image.SetPixel(cell.Column, cell.Row, color);
        _texture.Update(_image);
        Uploads++;
        return true;
    }

    public Vector3 CellCenter(HexCell cell)
    {
        (double x, double z) = HexGeometry.CellCenter(cell, CellRadius);
        return new Vector3((float)x, 0.0f, (float)z);
    }

    /// <summary>The cell under a window position, found where the camera's ray meets the ground plane. Null
    /// when the ray misses the plane, which an orthographic overhead camera never does.</summary>
    public static HexCell? Pick(Camera3D camera, Vector2 windowPosition)
    {
        Vector3 origin = camera.ProjectRayOrigin(windowPosition);
        Vector3 direction = camera.ProjectRayNormal(windowPosition);
        if (Mathf.Abs(direction.Y) < 1e-6f)
        {
            return null;
        }
        float distance = -origin.Y / direction.Y;
        if (distance < 0.0f)
        {
            return null;
        }
        Vector3 hit = origin + direction * distance;
        return HexGeometry.CellAt(hit.X, hit.Z, CellRadius);
    }
}
