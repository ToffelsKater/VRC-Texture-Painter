using System;
using UnityEngine;

namespace MeshTexturePainter
{
    /// <summary>Bounding volume hierarchy over a triangle soup for cursor ray casts.</summary>
    internal sealed class TriangleBVH
    {
        struct Node
        {
            public Vector3 min;
            public Vector3 max;
            public int start;   // first entry in order[] (leaf) or left child index (inner)
            public int count;   // > 0 for leaves
        }

        const int LeafSize = 4;

        readonly Vector3[] verts;   // 3 per triangle
        readonly int[] order;
        readonly Vector3[] centroids;
        readonly float[] keys;
        Node[] nodes;
        int nodeCount;

        public TriangleBVH(Vector3[] triangleVertices)
        {
            verts = triangleVertices;
            int triCount = verts.Length / 3;
            order = new int[triCount];
            centroids = new Vector3[triCount];
            keys = new float[triCount];
            for (int i = 0; i < triCount; i++)
            {
                order[i] = i;
                centroids[i] = (verts[i * 3] + verts[i * 3 + 1] + verts[i * 3 + 2]) / 3f;
            }
            nodes = new Node[Math.Max(1, triCount * 2)];
            nodeCount = 0;
            if (triCount > 0) Build(0, triCount);
        }

        int Build(int start, int count)
        {
            int index = nodeCount++;
            Vector3 bmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 bmax = -bmin;
            Vector3 cmin = bmin, cmax = bmax;
            for (int i = start; i < start + count; i++)
            {
                int t = order[i] * 3;
                for (int k = 0; k < 3; k++)
                {
                    bmin = Vector3.Min(bmin, verts[t + k]);
                    bmax = Vector3.Max(bmax, verts[t + k]);
                }
                cmin = Vector3.Min(cmin, centroids[order[i]]);
                cmax = Vector3.Max(cmax, centroids[order[i]]);
            }

            var node = new Node { min = bmin, max = bmax };
            Vector3 extent = cmax - cmin;
            if (count <= LeafSize || extent.sqrMagnitude < 1e-20f)
            {
                node.start = start;
                node.count = count;
                nodes[index] = node;
                return index;
            }

            int axis = extent.x > extent.y ? (extent.x > extent.z ? 0 : 2) : (extent.y > extent.z ? 1 : 2);
            for (int i = start; i < start + count; i++) keys[i] = centroids[order[i]][axis];
            Array.Sort(keys, order, start, count);

            int half = count / 2;
            int left = Build(start, half);
            int right = Build(start + half, count - half);
            node.start = left;
            node.count = 0;
            nodes[index] = node;
            // right child is always left + size of left subtree; store implicitly
            Debug.Assert(right > left);
            nodes[index].count = -right;
            return index;
        }

        /// <summary>Nearest two sided hit. Returns barycentric (u, v) of vertex 1 and 2.</summary>
        public bool Raycast(Ray ray, float maxDistance, out int triangle, out float distance, out float u, out float v)
        {
            triangle = -1;
            distance = maxDistance;
            u = v = 0f;
            if (nodeCount == 0) return false;

            Vector3 o = ray.origin;
            Vector3 d = ray.direction;
            Vector3 inv = new Vector3(1f / d.x, 1f / d.y, 1f / d.z);

            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                ref Node n = ref nodes[stack[--sp]];
                if (!SlabHit(n.min, n.max, o, inv, distance)) continue;
                if (n.count > 0)
                {
                    for (int i = n.start; i < n.start + n.count; i++)
                    {
                        int t = order[i];
                        if (IntersectTriangle(o, d, verts[t * 3], verts[t * 3 + 1], verts[t * 3 + 2], out float hitT, out float hu, out float hv)
                            && hitT < distance)
                        {
                            distance = hitT;
                            triangle = t;
                            u = hu;
                            v = hv;
                        }
                    }
                }
                else if (sp + 2 <= stack.Length)
                {
                    stack[sp++] = -n.count;
                    stack[sp++] = n.start;
                }
            }
            return triangle >= 0;
        }

        static bool SlabHit(Vector3 min, Vector3 max, Vector3 o, Vector3 inv, float maxT)
        {
            float t1 = (min.x - o.x) * inv.x, t2 = (max.x - o.x) * inv.x;
            float tmin = Math.Min(t1, t2), tmax = Math.Max(t1, t2);
            t1 = (min.y - o.y) * inv.y; t2 = (max.y - o.y) * inv.y;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
            t1 = (min.z - o.z) * inv.z; t2 = (max.z - o.z) * inv.z;
            tmin = Math.Max(tmin, Math.Min(t1, t2)); tmax = Math.Min(tmax, Math.Max(t1, t2));
            return tmax >= Math.Max(tmin, 0f) && tmin <= maxT;
        }

        static bool IntersectTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t, out float u, out float v)
        {
            t = u = v = 0f;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Math.Abs(det) < 1e-12f) return false;
            float invDet = 1f / det;
            Vector3 s = o - a;
            u = Vector3.Dot(s, p) * invDet;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            v = Vector3.Dot(d, q) * invDet;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) * invDet;
            return t > 0f;
        }
    }
}
