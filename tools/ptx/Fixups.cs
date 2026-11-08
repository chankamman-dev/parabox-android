using System.Text.RegularExpressions;

// The DXBC was compiled for D3D11, where depth is reversed (near = 1). GLES depth is not, so code
// that nudges clip-space z by hand moves things the wrong way. Those spots are rewritten here, per
// shader, on the GLSL HLSLcc produced.
static class Fixups
{
    static readonly (string shader, Regex find, string replace, string why)[] Rules =
    {
        // The outline pass pushes itself behind the mesh by subtracting from z; on GLES that pulls
        // it in front, and the outline covers the whole model (the black head in combat).
        ("FlatKit/Stylized Surface", new Regex(@"gl_Position\.z = \(-_OutlineDepthOffset\)"), "gl_Position.z = (_OutlineDepthOffset)", "outline depth offset sign"),
    };

    public static string Apply(string shader, string glsl, List<string> log)
    {
        foreach (var (s, find, replace, why) in Rules)
        {
            if (shader != s) continue;
            int n = find.Matches(glsl).Count;
            if (n == 0) continue;
            glsl = find.Replace(glsl, replace);
            log.Add($"{shader}: fixup '{why}' x{n}");
        }
        return glsl;
    }
}
