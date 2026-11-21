using System.Collections.Generic;
using UnityEngine;

/// <summary>Small helpers shared by the things built in code at runtime (people, traffic, the taxi).</summary>
public static class Shapes
{
    /// <summary>A copy of a URP Lit material in another colour, so no extra shader variants are needed in the build.</summary>
    public static Material Tinted(Material source, Color c, float smoothness, string name)
    {
        var m = new Material(source) { name = name };
        m.SetColor("_BaseColor", c);
        m.SetFloat("_Smoothness", smoothness);
        return m;
    }

    /// <summary>The vector with its height dropped.</summary>
    public static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);

    /// <summary>Adds a box (24 vertices, flat normals), optionally rotated around a pivot.</summary>
    public static void Box(List<Vector3> verts, List<Vector3> normals, List<int> tris, Vector3 center, Vector3 size,
        Quaternion? rotation = null, Vector3 pivot = default)
    {
        Quaternion rot = rotation ?? Quaternion.identity;
        Vector3 h = size / 2f;
        for (int axis = 0; axis < 3; axis++)
            for (int sign = -1; sign <= 1; sign += 2)
            {
                Vector3 n = Vector3.zero, u = Vector3.zero, v = Vector3.zero;
                n[axis] = sign;
                u[(axis + 1) % 3] = h[(axis + 1) % 3];
                v[(axis + 2) % 3] = h[(axis + 2) % 3];
                Vector3 f = center + n * h[axis];
                Vector3 p0 = f - u - v, p1 = f - u + v, p2 = f + u + v, p3 = f + u - v;

                int b = verts.Count;
                verts.Add(pivot + rot * (p0 - pivot));
                verts.Add(pivot + rot * (p1 - pivot));
                verts.Add(pivot + rot * (p2 - pivot));
                verts.Add(pivot + rot * (p3 - pivot));
                Vector3 rn = rot * n;
                for (int k = 0; k < 4; k++) normals.Add(rn);
                // Unity draws faces whose corners go clockwise seen from outside, i.e. Cross(b - a, c - a) points out.
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), n) > 0f)
                    tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                else
                    tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
    }
}
