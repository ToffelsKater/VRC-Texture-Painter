#ifndef MTP_WRAP_INCLUDED
#define MTP_WRAP_INCLUDED

// Wrap mode of the painted texture per axis, as UnityEngine.TextureWrapMode:
// 0 Repeat, 1 Clamp, 2 Mirror, 3 MirrorOnce.
float4 _WrapMode;

int WrapAxis(int i, int n, int mode)
{
    if (mode == 0)
    {
        i %= n;
        return i < 0 ? i + n : i;
    }
    if (mode == 2)
    {
        int period = 2 * n;
        i %= period;
        if (i < 0) i += period;
        return i >= n ? period - 1 - i : i;
    }
    if (mode == 3)
    {
        if (i < 0) i = -1 - i;
        return min(i, n - 1);
    }
    return clamp(i, 0, n - 1);
}

// Texel address the way the GPU sampler resolves it for this wrap mode.
int2 WrapTexel(int2 t, int2 size)
{
    return int2(WrapAxis(t.x, size.x, (int)_WrapMode.x), WrapAxis(t.y, size.y, (int)_WrapMode.y));
}

bool RepeatsU() { return (int)_WrapMode.x == 0; }
bool RepeatsV() { return (int)_WrapMode.y == 0; }

#endif
