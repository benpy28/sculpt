using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Coarser versions of the smooth ground for drawing far away (map v3 step 4). A coarse field keeps every
    /// <c>step</c>-th vertex and the same four-triangles-per-cell split, then lowers its vertices just enough that it
    /// never rises above the true surface anywhere: a far hill can look a little lower than it is, never higher, so the
    /// simpler ground can't hide a player lying behind a crest (Ben's rule: nothing hides a player without stopping
    /// bullets).
    ///
    /// Exact, not sampled: the true surface is flat on each fine triangle and the coarse one on each coarse triangle.
    /// Coarse cell borders run along fine grid lines and coarse diagonals along fine diagonals, so the two meshes laid on
    /// top of each other only have corners at fine vertices and fine cell centres. The gap between them is flat on each
    /// piece, so its largest value over a coarse cell is at one of those points. Lowering every corner of a coarse cell
    /// by at least that much lowers its centre (their mean) by as much, and so every point of the cell.
    /// </summary>
    public static class HeightFieldLod
    {
        /// <summary>
        /// The ground with one vertex every <paramref name="step"/> cells (cells <paramref name="step"/> times as big),
        /// never above <paramref name="f"/> (and at least <paramref name="margin"/> under it where it was lowered at all,
        /// so two levels never fight in the depth buffer). The field's cell counts must divide by the step.
        /// </summary>
        public static HeightField Coarse(HeightField f, int step, double margin = 0.02)
        {
            if (step < 1 || f.NX % step != 0 || f.NZ % step != 0) throw new ArgumentException("The step must divide the field's cell counts.");
            int nx = f.NX / step, nz = f.NZ / step;
            var c = new HeightField(f.OriginX, f.OriginZ, f.Cell * step, nx, nz);
            if (step == 1)
            {
                for (int j = 0; j <= nz; j++) for (int i = 0; i <= nx; i++) c.Set(i, j, f[i, j]);
                c.Finish();
                return c;
            }
            // How far each coarse cell's surface rises above the true one at worst.
            var over = new double[nx * nz];
            for (int cj = 0; cj < nz; cj++)
                for (int ci = 0; ci < nx; ci++)
                {
                    int i0 = ci * step, j0 = cj * step;
                    double h00 = f[i0, j0], h10 = f[i0 + step, j0], h11 = f[i0 + step, j0 + step], h01 = f[i0, j0 + step];
                    double hc = (h00 + h10 + h11 + h01) * 0.25;
                    double worst = 0;
                    for (int b = 0; b <= step; b++)
                        for (int a = 0; a <= step; a++)
                        {
                            // A fine vertex.
                            double g = At(h00, h10, h11, h01, hc, (double)a / step, (double)b / step) - f[i0 + a, j0 + b];
                            if (g > worst) worst = g;
                            if (a == step || b == step) continue;
                            // A fine cell centre.
                            double fc = (f[i0 + a, j0 + b] + f[i0 + a + 1, j0 + b] + f[i0 + a + 1, j0 + b + 1] + f[i0 + a, j0 + b + 1]) * 0.25;
                            g = At(h00, h10, h11, h01, hc, (a + 0.5) / step, (b + 0.5) / step) - fc;
                            if (g > worst) worst = g;
                        }
                    // Float rounding alone (a plane stored in floats) is a few micrometres: lowered by just that.
                    over[ci + nx * cj] = worst > 1e-4 ? worst + margin : worst;
                }
            for (int j = 0; j <= nz; j++)
                for (int i = 0; i <= nx; i++)
                {
                    double drop = 0;
                    for (int dj = -1; dj <= 0; dj++)
                        for (int di = -1; di <= 0; di++)
                        {
                            int ci = i + di, cj = j + dj;
                            if (ci < 0 || cj < 0 || ci >= nx || cj >= nz) continue;
                            drop = Math.Max(drop, over[ci + nx * cj]);
                        }
                    // Stored as floats: round down so the rounding can't lift it back over.
                    double h = f[i * step, j * step] - drop;
                    float hf = (float)h;
                    if (hf > h) hf = BitDown(hf);
                    c.Set(i, j, hf);
                }
            c.Finish();
            return c;
        }

        /// <summary>The four-triangle surface of one cell at local (u, v), from its corners and centre height.</summary>
        public static double At(double h00, double h10, double h11, double h01, double hc, double u, double v)
        {
            double du = u - 0.5, dv = v - 0.5;
            if (Math.Abs(dv) >= Math.Abs(du))
                return dv < 0 ? Blend(h00, h10, hc, u, -2 * dv) : Blend(h01, h11, hc, u, 2 * dv);
            return du > 0 ? Blend(h10, h11, hc, v, 2 * du) : Blend(h00, h01, hc, v, -2 * du);
        }

        // Height in a triangle (corner a at along 0, corner b at along 1, centre at w 0) at distance w from the centre
        // toward the edge ab (0 at the centre, 1 on the edge) and position along 0..1.
        private static double Blend(double a, double b, double hc, double along, double w)
        {
            // On the edge (w = 1): a + (b - a) * along. Toward the centre the edge shrinks to its midpoint, so this is
            // hc + w (a - hc) + w (b - a) / 2 + (b - a)(along - 1/2): flat over the triangle.
            return hc + w * (a - hc) + w * (b - a) * 0.5 + (b - a) * (along - 0.5);
        }

        private static float BitDown(float x)
        {
            int bits = BitConverter.SingleToInt32Bits(x);
            bits += x > 0 ? -1 : 1;
            return BitConverter.Int32BitsToSingle(bits);
        }
    }
}
