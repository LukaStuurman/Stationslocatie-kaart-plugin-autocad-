using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

namespace Stationslocaties;

internal sealed class StationPlacementJig(BlockReference preview) : DrawJig
{
    private bool orienting;

    internal bool Pick(Editor editor)
    {
        preview.Rotation = Math.PI;
        if (editor.Drag(this).Status != PromptStatus.OK) return false;
        orienting = true;
        var result = editor.Drag(this);
        if (result.Status == PromptStatus.None) preview.Rotation = Math.PI;
        return result.Status is PromptStatus.OK or PromptStatus.None;
    }

    protected override SamplerStatus Sampler(JigPrompts prompts)
    {
        var options = new JigPromptPointOptions(orienting
            ? "\nWijs naar de voorkant van de trafo (Enter voor oost): "
            : "\nKlik op het midden van de trafo: ")
        {
            UserInputControls = UserInputControls.Accept3dCoordinates
        };
        if (orienting)
        {
            options.BasePoint = preview.Position;
            options.UseBasePoint = true;
            options.Cursor = CursorType.RubberBand;
            options.UserInputControls |= UserInputControls.NullResponseAccepted;
        }
        var result = prompts.AcquirePoint(options);
        if (result.Status == PromptStatus.None && orienting) return SamplerStatus.NoChange;
        if (result.Status != PromptStatus.OK) return SamplerStatus.Cancel;
        var point = new Point3d(result.Value.X, result.Value.Y, 0);
        if (!orienting)
        {
            if (point.IsEqualTo(preview.Position)) return SamplerStatus.NoChange;
            preview.Position = point;
            return SamplerStatus.OK;
        }

        var direction = point - preview.Position;
        if (direction.Length < 1e-8) return SamplerStatus.NoChange;
        var angle = Math.Atan2(direction.Y, direction.X) + Math.PI;
        if (Math.Abs(angle - preview.Rotation) < 1e-10) return SamplerStatus.NoChange;
        preview.Rotation = angle;
        return SamplerStatus.OK;
    }

    protected override bool WorldDraw(WorldDraw draw)
    {
        draw.Geometry.Draw(preview);
        return true;
    }
}

