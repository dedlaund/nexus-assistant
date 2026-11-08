namespace NexusAssistant.Views;

internal sealed class AccretionScene
{
    public const int Width = 1024;
    public const int Height = 492;
    private const int TextureWidth = 1024;
    private const int TextureRows = 160;
    private const int OrbitSamples = 800;
    private const float AngleStep = .0105f;
    private const float ImpactStep = .004f;
    private const float MaxImpact = 25f;
    private readonly int[] _background = new int[Width * Height];
    private readonly List<Emission> _emission = [];
    private readonly List<Star> _stars = [];
    private readonly List<ShadowPixel> _shadow = [];
    private readonly float[] _texture = CreateTexture();

    private readonly record struct Emission(int Pixel, float Row, float Angle, float Speed, float Light, float Coverage);
    private readonly record struct Star(int Pixel, float Phase, float Speed, float Brightness);
    private readonly record struct ShadowPixel(int Pixel, float Radius);

    public AccretionScene()
    {
        var orbits = TraceOrbits();
        var light = new float[Width * Height];
        var random = new Random(841);
        const float pixelScale = 47f / Width;
        const float subpixel = pixelScale * .25f;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var px = (x - Width * .508f) * pixelScale;
                var py = (Height * .51f - y) * pixelScale;
                var dx = px * .993f + py * .118f;
                var dy = -px * .118f + py * .993f;
                var impact = MathF.Sqrt(dx * dx + dy * dy);
                var index = y * Width + x;
                var fade = Smooth(Math.Min(x, Width - 1 - x) / 120f) * Smooth(Math.Min(y, Height - 1 - y) / 88f);
                var dust = MathF.Exp(-MathF.Abs(dy + .26f) * .43f - MathF.Abs(dx) * .085f);
                var sky = impact < 5.17f ? 0 : dust * fade;
                var grain = (float)random.NextDouble();
                _background[index] = Pack(8 + sky * 8 + grain * fade, 9 + sky * 6 + grain * fade, 12 + sky * 5 + grain * fade);
                if (impact < 4.95f) _shadow.Add(new(index, impact));
                if (impact is < .1f or >= MaxImpact) continue;
                if (impact is > 4.9f and < 5.7f)
                {
                    // The narrow photon arcs need subpixel coverage to stay smooth as the disk moves.
                    light[index] = Trace(dx - subpixel, dy - subpixel, index, fade, .25f) +
                                   Trace(dx + subpixel, dy - subpixel, index, fade, .25f) +
                                   Trace(dx - subpixel, dy + subpixel, index, fade, .25f) +
                                   Trace(dx + subpixel, dy + subpixel, index, fade, .25f);
                }
                else light[index] = Trace(dx, dy, index, fade, 1);
            }
        }

        AddBloom(light, 3, 60, 31, 12);
        AddBloom(light, 12, 89, 39, 13);
        AddBloom(light, 32, 101, 43, 18);
        AddStars(random);

        float Trace(float dx, float dy, int pixel, float fade, float coverage)
        {
            var impact = MathF.Sqrt(dx * dx + dy * dy);
            var directionY = dy / impact;
            var crossing = MathF.Atan2(-.141f, directionY * .99f);
            if (crossing < 0) crossing += MathF.PI;
            var column = impact / ImpactStep;
            var orbit = (int)column * OrbitSamples;
            var orbitBlend = column - (int)column;
            for (var order = 0; order < 3; order++, crossing += MathF.PI)
            {
                var step = crossing / AngleStep;
                var sample = (int)step;
                if (sample + 1 >= OrbitSamples) break;
                var a = AtAngle(orbit + sample, step - sample);
                var b = AtAngle(orbit + OrbitSamples + sample, step - sample);
                var inverseRadius = a > 0 && b > 0 ? a + (b - a) * orbitBlend : orbitBlend < .5f ? a : b;
                if (inverseRadius <= 0) break;

                var radius = 1 / inverseRadius;
                if (radius is < 5.65f or > 22) continue;
                var azimuth = MathF.Atan2(.99f * MathF.Cos(crossing) - directionY * .141f * MathF.Sin(crossing),
                    dx / impact * MathF.Sin(crossing));
                var inner = Smooth((radius - 5.65f) / .95f);
                var outer = 1 - Smooth((radius - 12.5f) / 9.5f);
                var heat = MathF.Pow(7 / (radius + .6f), 1.55f);
                var shift = 1.03f - .16f * MathF.Cos(azimuth);
                var power = inner * outer * heat * shift * fade * (order == 0 ? 1 : order == 1 ? .84f : .39f);
                var row = Math.Clamp((radius - 5.65f) / 16.35f * TextureRows, 0, TextureRows - 1.01f);
                _emission.Add(new(pixel, row, (azimuth / MathF.Tau + 1) * TextureWidth,
                    128 / MathF.Pow(radius / 6, 1.45f), power, coverage));
                return power * coverage;
            }
            return 0;
        }

        float AtAngle(int index, float blend)
        {
            var near = orbits[index];
            var far = orbits[index + 1];
            return near > 0 && far > 0 ? near + (far - near) * blend : 0;
        }
    }

    public int[] NewFrame() => new int[_background.Length];

    public void Render(int[] pixels, float time)
    {
        Array.Copy(_background, pixels, pixels.Length);
        var breath = .5f + .5f * MathF.Sin(time * 1.35f);
        var shadowRadius = 3.85f + .75f * breath;
        foreach (var shadow in _shadow)
        {
            var depth = (1 - Smooth(shadow.Radius / shadowRadius)) * (4 + 3 * breath);
            var color = pixels[shadow.Pixel];
            pixels[shadow.Pixel] = Pack((color >> 16 & 255) - depth,
                (color >> 8 & 255) - depth * .94f, (color & 255) - depth * .85f);
        }
        foreach (var star in _stars)
        {
            var twinkle = .64f + .36f * MathF.Sin(time * star.Speed + star.Phase);
            var light = star.Brightness * twinkle;
            pixels[star.Pixel] = Add(pixels[star.Pixel], light * .87f, light * .94f, light);
        }
        var pulse = 1 + .035f * MathF.Sin(time * 2.1f);
        foreach (var ray in _emission)
        {
            var flow = ray.Angle + time * ray.Speed;
            var column = (int)flow;
            var row = (int)ray.Row;
            var current = TextureAt(row, column, flow - column);
            var next = TextureAt(row + 1, column, flow - column);
            var texture = current + (next - current) * (ray.Row - row);
            var shear = ray.Angle * 2 + time * ray.Speed * 2.8f + 197;
            var shearColumn = (int)shear;
            var filament = TextureAt(row, shearColumn, shear - shearColumn);
            var radiant = ray.Light * (texture * .84f + filament * filament * .22f) * pulse;
            var red = radiant * 360 / (1 + radiant * .48f);
            var green = radiant * (272 + 48 * Math.Min(radiant, 1)) / (1 + radiant * .47f);
            var blue = radiant * (152 + 123 * Math.Min(radiant, 1)) / (1 + radiant * .42f);
            pixels[ray.Pixel] = Add(pixels[ray.Pixel], red * ray.Coverage,
                green * ray.Coverage, blue * ray.Coverage);
        }
    }

    private float TextureAt(int row, int column, float blend)
    {
        var left = _texture[row * TextureWidth + (column & (TextureWidth - 1))];
        var right = _texture[row * TextureWidth + ((column + 1) & (TextureWidth - 1))];
        return left + (right - left) * blend;
    }

    private static float[] TraceOrbits()
    {
        var count = (int)(MaxImpact / ImpactStep) + 2;
        var result = new float[count * OrbitSamples];
        Parallel.For(1, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) }, index =>
        {
            // In units GM/c², the Schwarzschild null orbit satisfies u″ + u = 3u², u = 1/r.
            var position = 0f;
            var slope = 1 / (index * ImpactStep);
            for (var step = 1; step < OrbitSamples; step++)
            {
                var a = Acceleration(position);
                var middle = position + slope * AngleStep * .5f;
                var middleSlope = slope + a * AngleStep * .5f;
                position += middleSlope * AngleStep;
                slope += Acceleration(middle) * AngleStep;
                if (position <= 0 || position >= .5f) break;
                result[index * OrbitSamples + step] = position;
            }
        });
        return result;
    }

    private static float Acceleration(float u) => 3 * u * u - u;

    private static float[] CreateTexture()
    {
        var random = new Random(382);
        var texture = new float[TextureWidth * TextureRows];
        var cloud = new float[256 * 128];
        for (var i = 0; i < cloud.Length; i++) cloud[i] = (float)random.NextDouble();

        for (var y = 0; y < TextureRows; y++)
        {
            for (var x = 0; x < TextureWidth; x++)
            {
                var u = x / (float)TextureWidth;
                var warp = y * .071f + 2 * Noise(u * 8, y * .055f, 8);
                var broad = Noise(u * 16 + warp, y * .133f, 16);
                var fine = Noise(u * 64 + warp * 3, y * .50f, 64);
                var wisps = Noise(u * 128 + warp * 5.1f, y * 1.1f, 128);
                var ring = .82f + .15f * MathF.Sin(y * 1.13f + broad * 5) + .06f * MathF.Sin(y * 2.14f + fine * 2);
                texture[y * TextureWidth + x] = Math.Max(.24f, ring * (.45f + .96f * broad + .44f * fine + .20f * wisps));
            }
        }
        return texture;

        float Noise(float x, float y, int period)
        {
            var i = (int)x;
            var j = (int)y;
            var a = Smooth(x - i);
            var b = Smooth(y - j);
            var p = i & (period - 1);
            var q = (i + 1) & (period - 1);
            var row = (j & 127) * 256;
            var next = ((j + 1) & 127) * 256;
            var first = cloud[row + p] * (1 - a) + cloud[row + q] * a;
            var second = cloud[next + p] * (1 - a) + cloud[next + q] * a;
            return first * (1 - b) + second * b;
        }
    }

    private void AddBloom(float[] light, int radius, float r, float g, float b)
    {
        var horizontal = new float[light.Length];
        var span = radius * 2 + 1;
        for (var y = 0; y < Height; y++)
        {
            var start = y * Width;
            var sum = 0f;
            for (var x = -radius; x < Width; x++)
            {
                if (x + radius < Width) sum += light[start + x + radius];
                if (x - radius - 1 >= 0) sum -= light[start + x - radius - 1];
                if (x >= 0) horizontal[start + x] = sum / span;
            }
        }
        for (var x = 0; x < Width; x++)
        {
            var sum = 0f;
            for (var y = -radius; y < Height; y++)
            {
                if (y + radius < Height) sum += horizontal[(y + radius) * Width + x];
                if (y - radius - 1 >= 0) sum -= horizontal[(y - radius - 1) * Width + x];
                if (y < 0) continue;
                var index = y * Width + x;
                var value = Math.Max(0, sum / span);
                _background[index] = Add(_background[index], r * value, g * value, b * value);
            }
        }
    }

    private void AddStars(Random random)
    {
        for (var i = 0; i < 520; i++)
        {
            var x = random.Next(2, Width - 2);
            var y = random.Next(2, Height - 2);
            var dx = x - Width * .508f;
            var dy = y - Height * .48f;
            if (dx * dx + dy * dy < 117 * 117) continue;
            var value = 5 + (float)Math.Pow(random.NextDouble(), 4) * 50;
            var index = y * Width + x;
            if (value > 20)
                _stars.Add(new(index, (float)random.NextDouble() * MathF.Tau, .4f + (float)random.NextDouble() * 1.2f, value));
            else
                _background[index] = Add(_background[index], value * .86f, value * .94f, value);
            if (value > 42) _background[index + 1] = Add(_background[index + 1], value * .22f, value * .23f, value * .26f);
        }
    }

    private static float Smooth(float value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    private static int Add(int color, float r, float g, float b) =>
        Pack((color >> 16 & 255) + r, (color >> 8 & 255) + g, (color & 255) + b);

    private static int Pack(float r, float g, float b) =>
        (int)Math.Min(255, r) << 16 | (int)Math.Min(255, g) << 8 | (int)Math.Min(255, b);
}
