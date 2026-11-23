using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace SaudeNotebook
{
    public class Lancamento
    {
        public Version Versao;
        public string Tag;
        public string Notas;
        public string UrlExe;
        public string UrlPagina;
    }

    /// <summary>Confere e instala versões novas publicadas no GitHub Releases.</summary>
    public static class Atualizador
    {
        public const string Repo = "stackkzz/SaudeNotebook";
        public static string PaginaReleases => $"https://github.com/{Repo}/releases";

        public static Version Atual
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static string AtualTexto => $"{Atual.Major}.{Atual.Minor}.{Atual.Build}";

        static HttpClient NovoCliente()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SaudeNotebook/" + AtualTexto);
            return http;
        }

        public static async Task<Lancamento> VerificarAsync()
        {
            using (var http = NovoCliente())
            {
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                using (var doc = JsonDocument.Parse(json))
                {
                    var raiz = doc.RootElement;
                    string tag = raiz.GetProperty("tag_name").GetString() ?? "";
                    if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
                    string url = null;
                    foreach (var a in raiz.GetProperty("assets").EnumerateArray())
                    {
                        string nome = a.GetProperty("name").GetString() ?? "";
                        if (nome.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            url = a.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                    return new Lancamento
                    {
                        Versao = new Version(v.Major, v.Minor, Math.Max(0, v.Build)),
                        Tag = tag,
                        Notas = raiz.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                        UrlExe = url,
                        UrlPagina = raiz.TryGetProperty("html_url", out var h) ? h.GetString() : PaginaReleases
                    };
                }
            }
        }

        public static bool EhMaisNova(Lancamento l) => l != null && l.Versao > Atual;

        /// <summary>Baixa o .exe novo, troca pelo atual e abre a versão nova. Retorna false se não conseguiu.</summary>
        public static async Task<bool> InstalarAsync(Lancamento l, IProgress<double> progresso)
        {
            if (l?.UrlExe == null) return false;
            string exe = Environment.ProcessPath;
            string pasta = Path.GetDirectoryName(exe);
            string novo = Path.Combine(pasta, "SaudeNotebook.novo.exe");
            string velho = Path.Combine(pasta, "SaudeNotebook.old.exe");

            using (var http = NovoCliente())
            using (var resp = await http.GetAsync(l.UrlExe, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;
                using (var origem = await resp.Content.ReadAsStreamAsync())
                using (var destino = File.Create(novo))
                {
                    var buf = new byte[81920];
                    long lidos = 0;
                    int n;
                    while ((n = await origem.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await destino.WriteAsync(buf, 0, n);
                        lidos += n;
                        if (total > 0) progresso?.Report(100.0 * lidos / total);
                    }
                }
            }

            if (new FileInfo(novo).Length < 1_000_000) { File.Delete(novo); return false; }

            if (File.Exists(velho)) File.Delete(velho);
            File.Move(exe, velho);          // o Windows deixa renomear o .exe em uso
            File.Move(novo, exe);

            App.LiberarInstancia();
            Process.Start(new ProcessStartInfo(exe, "--atualizado") { UseShellExecute = true });
            return true;
        }

        /// <summary>Apaga a cópia da versão anterior deixada pela atualização.</summary>
        public static void LimparAntigo()
        {
            try
            {
                string pasta = Path.GetDirectoryName(Environment.ProcessPath);
                foreach (var f in new[] { "SaudeNotebook.old.exe", "SaudeNotebook.novo.exe" })
                {
                    string p = Path.Combine(pasta, f);
                    if (File.Exists(p)) File.Delete(p);
                }
            }
            catch { }
        }
    }
}
