using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace SaudeNotebook
{
    public static class Relatorio
    {
        /// <summary>Diagnóstico completo, pronto para colar no Claude.</summary>
        public static string TextoClaude(Dados d, List<Alerta> alertas, ResultadoEngasgos eng, List<Achado> scan)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Oi Claude! O app Saúde do Notebook gerou este diagnóstico do meu PC. Me diz o que está pesando ou errado, em ordem de prioridade, e como resolver cada coisa passo a passo.");
            sb.AppendLine();
            sb.AppendLine("## Máquina");
            sb.AppendLine(Contexto.Maquina);
            sb.AppendLine(Txt.F("App versão {0} • {1:dd/MM/yyyy HH:mm} • {2}", Atualizador.AtualTexto, DateTime.Now, Nativo.EhAdmin() ? "rodando como administrador" : "sem administrador"));

            if (d != null)
            {
                sb.AppendLine();
                sb.AppendLine("## Agora");
                sb.AppendLine(Txt.F("- CPU {0:N0}% (clock {1:N0}% do nominal) • RAM {2:N0}% ({3:N1} de {4:N1} GB livres) • GPU {5:N0}% • Disco {6:N0}% ocupado",
                    d.Cpu, d.DesempenhoPct, d.RamPct, d.RamLivreGB, d.RamTotalGB, Math.Max(0, d.Gpu), d.DiscoPct));
                sb.AppendLine(Txt.F("- Rede: envio {0} • recebe {1}", Txt.Taxa(d.RedeEnvioKBs), Txt.Taxa(d.RedeRecebKBs)));
                if (d.TemBateria)
                    sb.AppendLine(Txt.F("- Bateria {0}% • {1} • saúde {2}% • {3} ciclos", d.BatPct,
                        d.NaTomada ? "na tomada" : Txt.F("gastando {0:N1} W", d.DescargaW), d.BatSaude, d.BatCiclos));
                if (d.TempC > 0) sb.AppendLine(Txt.F("- Temperatura (sensor ACPI): {0} °C", d.TempC));
                sb.AppendLine("- Ligado há " + Txt.Horas(d.LigadoHoras));
            }

            if (eng != null)
            {
                sb.AppendLine();
                sb.AppendLine("## Memória");
                sb.AppendLine(eng.MemDetalhe);
                sb.AppendLine(eng.MemCulpados);
                sb.AppendLine();
                sb.AppendLine("## Engasgos");
                sb.AppendLine(eng.Veredito);
                foreach (var l in eng.Linhas.Where(x => Txt.Nivel(x.Severidade) >= 2))
                    sb.AppendLine($"- [{l.Severidade}] {l.Item}: {l.Valor}");
                sb.AppendLine(eng.Culpados);
            }

            sb.AppendLine();
            sb.AppendLine("## Alertas ativos");
            if (alertas == null || alertas.Count == 0) sb.AppendLine("- Nenhum.");
            else foreach (var a in alertas) sb.AppendLine($"- [{a.Severidade}] {a.Problema}: {a.Detalhe}");

            if (d != null)
            {
                sb.AppendLine();
                sb.AppendLine("## Programas que mais consomem (somando processos do mesmo programa)");
                var grupos = d.TodosProcs.GroupBy(p => p.Nome, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new { Nome = g.Key, Qtd = g.Count(), Cpu = g.Sum(p => p.Cpu), Gpu = Math.Min(100, g.Sum(p => p.Gpu)), Ram = g.Sum(p => p.RamMB) })
                    .Where(g => g.Nome != "Idle" && g.Nome != "_Total")
                    .OrderByDescending(g => g.Cpu * 3 + g.Gpu * 2 + g.Ram / 200).Take(12);
                foreach (var g in grupos)
                    sb.AppendLine(Txt.F("- {0}{1}: CPU {2:N1}% • GPU {3:N0}% • RAM {4:N0} MB", g.Nome, g.Qtd > 1 ? " ×" + g.Qtd : "", g.Cpu, g.Gpu, g.Ram));
            }

            sb.AppendLine();
            sb.AppendLine("## Varredura de segurança e saúde (só os problemas)");
            var probs = scan?.Where(x => Txt.Nivel(x.Severidade) >= 2).ToList();
            if (scan == null || scan.Count == 0) sb.AppendLine("- Ainda não rodou.");
            else if (probs.Count == 0) sb.AppendLine("- Nenhum problema encontrado.");
            else foreach (var x in probs) sb.AppendLine($"- [{x.Severidade}] {x.Categoria} — {x.Item}: {x.Detalhe}");

            return sb.ToString();
        }

        public static string Html(Dados d, List<Alerta> alertas, ResultadoEngasgos eng, List<Achado> scan, string status)
        {
            string H(object s) => WebUtility.HtmlEncode(s?.ToString() ?? "");
            var cor = new Dictionary<string, string> { { "Crítico", "#d93636" }, { "Atenção", "#b57900" }, { "Info", "#2f6fd6" }, { "OK", "#1f9d55" } };
            var sb = new StringBuilder();

            void Tabela(string titulo, IEnumerable<object> linhas, params (string Cab, Func<object, string> Val, bool Sev)[] cols)
            {
                sb.Append("<h2>").Append(H(titulo)).Append("</h2>");
                var lista = linhas?.ToList() ?? new List<object>();
                if (lista.Count == 0) { sb.Append("<p class='vazio'>Sem dados.</p>"); return; }
                sb.Append("<table><tr>");
                foreach (var c in cols) sb.Append("<th>").Append(H(c.Cab)).Append("</th>");
                sb.Append("</tr>");
                foreach (var l in lista)
                {
                    sb.Append("<tr>");
                    foreach (var c in cols)
                    {
                        string v = c.Val(l);
                        string corSev = cor.ContainsKey(v ?? "") ? cor[v] : "#333";
                        if (c.Sev) sb.Append("<td style='color:").Append(corSev).Append(";font-weight:600'>").Append(H(v)).Append("</td>");
                        else sb.Append("<td>").Append(H(v)).Append("</td>");
                    }
                    sb.Append("</tr>");
                }
                sb.Append("</table>");
            }

            sb.Append("<!DOCTYPE html><html lang='pt-BR'><head><meta charset='utf-8'><title>Relatório - Saúde do Notebook</title><style>");
            sb.Append("body{font-family:Segoe UI,Arial,sans-serif;margin:32px auto;max-width:1100px;color:#1d2330;padding:0 16px}");
            sb.Append("h1{margin:0 0 4px}h2{margin-top:32px;border-bottom:2px solid #e3e6ec;padding-bottom:6px}");
            sb.Append(".sub{color:#5b6475}table{border-collapse:collapse;width:100%;font-size:14px}");
            sb.Append("th{background:#f1f3f7;text-align:left;padding:8px;color:#4a5263}td{padding:8px;border-top:1px solid #eceef2;vertical-align:top}");
            sb.Append(".vazio{color:#8a92a3}pre{white-space:pre-wrap;font-family:inherit}</style></head><body>");
            sb.Append("<h1>Saúde do Notebook</h1><p class='sub'>").Append(H(Contexto.Maquina)).Append("<br>")
              .Append(H(Txt.F("Gerado em {0:dd/MM/yyyy HH:mm} • versão {1} • status: {2}", DateTime.Now, Atualizador.AtualTexto, status))).Append("</p>");

            Tabela("Alertas ao vivo", alertas?.Cast<object>(),
                ("Nível", o => ((Alerta)o).Severidade, true), ("Problema", o => ((Alerta)o).Problema, false), ("Detalhe", o => ((Alerta)o).Detalhe, false));
            if (eng != null)
            {
                sb.Append("<h2>Memória em detalhe</h2><pre>").Append(H(eng.MemDetalhe + "\n" + eng.MemCulpados)).Append("</pre>");
                Tabela("Engasgos", eng.Linhas.Cast<object>(),
                    ("Item", o => ((LinhaEngasgo)o).Item, false), ("Agora", o => ((LinhaEngasgo)o).Valor, false),
                    ("Nível", o => ((LinhaEngasgo)o).Severidade, true), ("O que significa", o => ((LinhaEngasgo)o).Explicacao, false));
            }
            if (scan != null && scan.Count > 0)
                Tabela("Varredura de segurança e saúde", scan.Cast<object>(),
                    ("Nível", o => ((Achado)o).Severidade, true), ("Categoria", o => ((Achado)o).Categoria, false), ("Item", o => ((Achado)o).Item, false),
                    ("Detalhe", o => ((Achado)o).Detalhe, false), ("O que fazer", o => ((Achado)o).Recomendacao, false));
            else sb.Append("<h2>Varredura de segurança e saúde</h2><p class='vazio'>Ainda não rodou.</p>");
            sb.Append("</body></html>");

            Directory.CreateDirectory(Pastas.Relatorios);
            string arq = Path.Combine(Pastas.Relatorios, Txt.F("relatorio-{0:yyyy-MM-dd_HHmm}.html", DateTime.Now));
            File.WriteAllText(arq, sb.ToString(), new UTF8Encoding(true));
            return arq;
        }

        public static void GravarHistorico(Dados d, List<Alerta> alertas)
        {
            try
            {
                Directory.CreateDirectory(Pastas.Dados);
                string arq = Path.Combine(Pastas.Dados, Txt.F("historico-{0:yyyy-MM}.csv", DateTime.Now));
                bool novo = !File.Exists(arq);
                var c = Analise.Top(d, p => p.Cpu);
                var g = Analise.Top(d, p => p.Gpu);
                string Esc(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
                var linha = string.Join(";", new[]
                {
                    Txt.F("{0:yyyy-MM-dd HH:mm:ss}", DateTime.Now), Txt.F("{0:N1}", d.Cpu), Txt.F("{0:N1}", d.RamPct), Txt.F("{0:N1}", d.Gpu),
                    Txt.F("{0:N1}", d.DiscoPct), Txt.F("{0:N1}", d.RedeEnvioKBs), Txt.F("{0:N1}", d.RedeRecebKBs), d.NaTomada ? "sim" : "não",
                    d.BatPct.ToString(), Txt.F("{0:N1}", d.DescargaW), Txt.F("{0:N0}", d.TempC), Txt.F("{0:N0}", d.PaginasSeg), d.OciosoSeg.ToString(),
                    Esc(c?.Nome), Esc(g != null && g.Gpu > 0 ? g.Nome : ""), Esc(string.Join(" | ", alertas.Select(a => a.Problema)))
                });
                using (var w = new StreamWriter(arq, true, new UTF8Encoding(novo)))
                {
                    if (novo) w.WriteLine("Hora;CPU;RAM;GPU;Disco;EnvioKBs;RecebKBs;NaTomada;BateriaPct;BateriaW;TempC;PaginasDiscoSeg;OciosoSeg;TopCPU;TopGPU;Alertas");
                    w.WriteLine(linha);
                }
            }
            catch { }
        }
    }
}
