using System.Text;

namespace Aero.Core.Sim;

/// <summary>
/// Generates an interactive HTML report from flight simulation history.
/// Self-contained — uses Chart.js from CDN. Just open in a browser.
/// </summary>
public static class FlightReport
{
    /// <summary>
    /// Generate an HTML report and write it to the given path.
    /// Returns the full path for convenience.
    /// </summary>
    public static string Generate(List<FlightRecord> history, string outputPath, string title = "Flight Sim Report")
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head>");
        sb.AppendLine($"<title>{title}</title>");
        sb.AppendLine("<script src=\"https://cdn.jsdelivr.net/npm/chart.js\"></script>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: 'Segoe UI', sans-serif; background: #1a1a2e; color: #e0e0e0; margin: 0; padding: 20px; }");
        sb.AppendLine("h1 { color: #00d4ff; margin-bottom: 5px; }");
        sb.AppendLine("h2 { color: #7b68ee; margin-top: 30px; margin-bottom: 10px; }");
        sb.AppendLine(".subtitle { color: #888; margin-bottom: 20px; }");
        sb.AppendLine(".stats { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 12px; margin: 20px 0; }");
        sb.AppendLine(".stat { background: #16213e; border-radius: 8px; padding: 12px; border-left: 3px solid #00d4ff; }");
        sb.AppendLine(".stat-label { color: #888; font-size: 12px; text-transform: uppercase; }");
        sb.AppendLine(".stat-value { color: #fff; font-size: 22px; font-weight: bold; margin-top: 4px; }");
        sb.AppendLine(".chart-row { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin: 10px 0; }");
        sb.AppendLine(".chart-box { background: #16213e; border-radius: 8px; padding: 16px; }");
        sb.AppendLine("@media (max-width: 900px) { .chart-row { grid-template-columns: 1fr; } }");
        sb.AppendLine("canvas { max-height: 300px; }");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine($"<h1>{title}</h1>");

        if (history.Count == 0)
        {
            sb.AppendLine("<p>No flight data recorded.</p></body></html>");
            File.WriteAllText(outputPath, sb.ToString());
            return Path.GetFullPath(outputPath);
        }

        var first = history[0];
        var last = history[^1];
        var peakAlt = history.Max(r => r.Altitude);
        var peakMach = history.Max(r => r.Mach);
        var peakQ = history.Max(r => r.DynamicPressure);
        var peakDrag = history.Max(r => r.Aero.DragMagnitude);
        var peakSpeed = history.Max(r => r.State.Speed);

        sb.AppendLine($"<div class=\"subtitle\">{history.Count} records, {last.Time:F1}s duration</div>");

        // Stats grid
        sb.AppendLine("<div class=\"stats\">");
        Stat(sb, "Peak Altitude", $"{peakAlt:F0} m", peakAlt > 1000 ? $"({peakAlt / 1000:F1} km)" : "");
        Stat(sb, "Peak Speed", $"{peakSpeed:F0} m/s", $"({peakSpeed * 3.6:F0} km/h)");
        Stat(sb, "Peak Mach", $"{peakMach:F2}");
        Stat(sb, "Peak q", $"{peakQ:F0} Pa");
        Stat(sb, "Peak Drag", $"{peakDrag:F0} N");
        Stat(sb, "Final Speed", $"{last.State.Speed:F1} m/s");
        Stat(sb, "Final Alt", $"{last.Altitude:F0} m");
        Stat(sb, "Duration", $"{last.Time:F1} s");
        sb.AppendLine("</div>");

        // Emit data as JS arrays
        sb.AppendLine("<script>");
        EmitArray(sb, "time", history, r => r.Time);
        EmitArray(sb, "altitude", history, r => r.Altitude);
        EmitArray(sb, "speed", history, r => r.State.Speed);
        EmitArray(sb, "mach", history, r => r.Mach);
        EmitArray(sb, "dynPressure", history, r => r.DynamicPressure);
        EmitArray(sb, "drag", history, r => r.Aero.DragMagnitude);
        EmitArray(sb, "lift", history, r => r.Aero.LiftMagnitude);
        EmitArray(sb, "aoa", history, r => r.AngleOfAttack);
        EmitArray(sb, "density", history, r => r.Atmosphere.Density);
        EmitArray(sb, "angSpeed", history, r => r.State.AngularSpeed);
        EmitArray(sb, "posX", history, r => r.State.Position.X);
        EmitArray(sb, "posY", history, r => r.State.Position.Y);
        EmitArray(sb, "posZ", history, r => r.State.Position.Z);

        // Chart creation helper
        sb.AppendLine(@"
function makeChart(id, datasets, yLabel) {
    const ctx = document.getElementById(id).getContext('2d');
    const colors = ['#00d4ff','#ff6384','#ffce56','#4bc0c0','#9966ff','#ff9f40'];
    new Chart(ctx, {
        type: 'line',
        data: {
            labels: time.map(t => t.toFixed(1)),
            datasets: datasets.map((ds, i) => ({
                label: ds.label,
                data: ds.data,
                borderColor: colors[i % colors.length],
                borderWidth: 1.5,
                pointRadius: 0,
                tension: 0.3,
                fill: false,
            }))
        },
        options: {
            responsive: true,
            animation: false,
            scales: {
                x: { title: { display: true, text: 'Time (s)', color: '#888' },
                     ticks: { color: '#666', maxTicksLimit: 15 }, grid: { color: '#333' } },
                y: { title: { display: true, text: yLabel, color: '#888' },
                     ticks: { color: '#666' }, grid: { color: '#333' } }
            },
            plugins: { legend: { labels: { color: '#ccc' } } }
        }
    });
}");
        sb.AppendLine("</script>");

        // Charts
        sb.AppendLine("<div class=\"chart-row\">");
        ChartBox(sb, "chartAlt", "Altitude");
        ChartBox(sb, "chartSpeed", "Speed & Mach");
        sb.AppendLine("</div><div class=\"chart-row\">");
        ChartBox(sb, "chartForces", "Aerodynamic Forces");
        ChartBox(sb, "chartQ", "Dynamic Pressure & Density");
        sb.AppendLine("</div><div class=\"chart-row\">");
        ChartBox(sb, "chartAoA", "Angle of Attack");
        ChartBox(sb, "chartAngular", "Angular Speed");
        sb.AppendLine("</div><div class=\"chart-row\">");
        ChartBox(sb, "chartTrajectory", "Trajectory (X vs Y)");
        ChartBox(sb, "chartMach", "Mach Number");
        sb.AppendLine("</div>");

        // Chart init
        sb.AppendLine("<script>");
        sb.AppendLine("makeChart('chartAlt', [{label:'Altitude (m)',data:altitude}], 'm');");
        sb.AppendLine("makeChart('chartSpeed', [{label:'Speed (m/s)',data:speed}], 'm/s');");
        sb.AppendLine("makeChart('chartMach', [{label:'Mach',data:mach}], 'Mach');");
        sb.AppendLine("makeChart('chartForces', [{label:'Drag (N)',data:drag},{label:'Lift (N)',data:lift}], 'N');");
        sb.AppendLine("makeChart('chartQ', [{label:'q (Pa)',data:dynPressure}], 'Pa');");
        sb.AppendLine("makeChart('chartAoA', [{label:'AoA (°)',data:aoa}], '°');");
        sb.AppendLine("makeChart('chartAngular', [{label:'ω (rad/s)',data:angSpeed}], 'rad/s');");

        // Trajectory chart is X vs Y, not time-based
        sb.AppendLine(@"
(function() {
    const ctx = document.getElementById('chartTrajectory').getContext('2d');
    new Chart(ctx, {
        type: 'scatter',
        data: {
            datasets: [{
                label: 'X vs Alt',
                data: posX.map((x,i) => ({x: x, y: posY[i]})),
                borderColor: '#00d4ff',
                showLine: true,
                borderWidth: 1.5,
                pointRadius: 0,
                tension: 0.3,
                fill: false,
            }]
        },
        options: {
            responsive: true,
            animation: false,
            scales: {
                x: { title: { display: true, text: 'X (m)', color: '#888' },
                     ticks: { color: '#666' }, grid: { color: '#333' } },
                y: { title: { display: true, text: 'Altitude (m)', color: '#888' },
                     ticks: { color: '#666' }, grid: { color: '#333' } }
            },
            plugins: { legend: { labels: { color: '#ccc' } } }
        }
    });
})();");
        sb.AppendLine("</script>");

        sb.AppendLine("</body></html>");

        File.WriteAllText(outputPath, sb.ToString());
        return Path.GetFullPath(outputPath);
    }

    private static void Stat(StringBuilder sb, string label, string value, string sub = "")
    {
        sb.AppendLine($"<div class=\"stat\"><div class=\"stat-label\">{label}</div>");
        sb.AppendLine($"<div class=\"stat-value\">{value}</div>");
        if (!string.IsNullOrEmpty(sub))
            sb.AppendLine($"<div style=\"color:#666;font-size:12px\">{sub}</div>");
        sb.AppendLine("</div>");
    }

    private static void EmitArray(StringBuilder sb, string name, List<FlightRecord> history, Func<FlightRecord, double> selector)
    {
        sb.Append($"const {name} = [");
        for (int i = 0; i < history.Count; i++)
        {
            if (i > 0) sb.Append(',');
            double v = selector(history[i]);
            if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
            sb.Append(v.ToString("G6"));
        }
        sb.AppendLine("];");
    }

    private static void EmitArray(StringBuilder sb, string name, List<FlightRecord> history, Func<FlightRecord, float> selector)
    {
        EmitArray(sb, name, history, r => (double)selector(r));
    }

    private static void ChartBox(StringBuilder sb, string id, string title)
    {
        sb.AppendLine($"<div class=\"chart-box\"><h2 style=\"margin-top:0\">{title}</h2>");
        sb.AppendLine($"<canvas id=\"{id}\"></canvas></div>");
    }
}
