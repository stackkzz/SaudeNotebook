using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace SaudeNotebook
{
    /// <summary>
    /// Servidor MCP (Model Context Protocol) por stdin/stdout.
    /// O app do Claude abre "SaudeNotebook.exe --mcp" e chama estas ferramentas para ler o estado do PC.
    /// Só leitura: nenhuma ferramenta muda nada no computador.
    /// </summary>
    public static class McpServidor
    {
        static readonly ColetorAoVivo Coletor = new ColetorAoVivo();
        static readonly Analise Analisador = new Analise();
        static TextWriter _saida;

        const string Instrucoes =
            "Ferramentas do app Saúde do Notebook, que roda no computador Windows do usuário. " +
            "Use para ver CPU, RAM, GPU, disco, rede, bateria, processos, memória em detalhe, histórico e a varredura de segurança. " +
            "Tudo é só leitura. Os números são do momento da chamada; para tendências use 'historico'.";

        public static void Rodar()
        {
            var entrada = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            _saida = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            string linha;
            while ((linha = entrada.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(linha)) continue;
                JsonNode msg;
                try { msg = JsonNode.Parse(linha); }
                catch { Erro(null, -32700, "JSON inválido"); continue; }
                if (msg == null) continue;

                var id = msg["id"];
                string metodo = Texto(msg["method"]);
                if (metodo == null || id == null) continue; // respostas e notificações não precisam de resposta

                try
                {
                    switch (metodo)
                    {
                        case "initialize":
                            string versaoProtocolo = Texto(msg["params"]?["protocolVersion"]) ?? "2025-06-18";
                            Responder(id, new JsonObject
                            {
                                ["protocolVersion"] = versaoProtocolo,
                                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                                ["serverInfo"] = new JsonObject { ["name"] = "saude-notebook", ["version"] = Atualizador.AtualTexto },
                                ["instructions"] = Instrucoes
                            });
                            break;
                        case "ping":
                            Responder(id, new JsonObject());
                            break;
                        case "tools/list":
                            Responder(id, new JsonObject { ["tools"] = ListaFerramentas() });
                            break;
                        case "tools/call":
                            string nome = Texto(msg["params"]?["name"]) ?? "";
                            var args = msg["params"]?["arguments"] as JsonObject ?? new JsonObject();
                            string resultado;
                            bool falhou = false;
                            try { resultado = Executar(nome, args); }
                            catch (Exception ex) { resultado = "Erro: " + ex.Message; falhou = true; }
                            Responder(id, new JsonObject
                            {
                                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = resultado }),
                                ["isError"] = falhou
                            });
                            break;
                        default:
                            Erro(id, -32601, "Método não suportado: " + metodo);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Erro(id, -32603, ex.Message);
                }
            }
        }

        static string Texto(JsonNode n)
        {
            try { return n?.GetValue<string>(); } catch { return null; }
        }

        static void Responder(JsonNode id, JsonNode resultado)
        {
            var r = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = resultado };
            lock (_saida) _saida.WriteLine(r.ToJsonString());
        }

        static void Erro(JsonNode id, int codigo, string mensagem)
        {
            var r = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = codigo, ["message"] = mensagem }
            };
            lock (_saida) _saida.WriteLine(r.ToJsonString());
        }

        // ================================================================
        //  Ferramentas
        // ================================================================
        static JsonObject Ferramenta(string nome, string titulo, string descricao, JsonObject propriedades = null)
        {
            return new JsonObject
            {
                ["name"] = nome,
                ["title"] = titulo,
                ["description"] = descricao,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = propriedades ?? new JsonObject()
                },
                ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false }
            };
        }

        static JsonArray ListaFerramentas()
        {
            return new JsonArray(
                Ferramenta("estado_atual", "Estado atual do PC",
                    "Resumo de agora: CPU, RAM, GPU, disco, rede, bateria, temperatura, alertas ativos e diagnóstico de engasgos."),
                Ferramenta("processos", "Processos",
                    "Lista os processos que mais consomem, já ordenados. Pode somar os processos de um mesmo programa (ex.: todas as abas do Chrome).",
                    new JsonObject
                    {
                        ["ordenar_por"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("cpu", "gpu", "ram", "pedindo_memoria", "disco"),
                            ["description"] = "cpu, gpu, ram (memória ocupada), pedindo_memoria (falhas de página por segundo) ou disco. Padrão: cpu."
                        },
                        ["agrupar"] = new JsonObject { ["type"] = "boolean", ["description"] = "Somar processos do mesmo programa. Padrão: true." },
                        ["limite"] = new JsonObject { ["type"] = "integer", ["description"] = "Quantos mostrar (1 a 100). Padrão: 25." }
                    }),
                Ferramenta("memoria_detalhada", "Memória em detalhe",
                    "Para onde vai a RAM: programas, drivers/kernel (pools), compactada, cache, arquivo de paginação, pentes e slots, quem mais ocupa e quem mais pede memória."),
                Ferramenta("historico", "Histórico",
                    "Resumo das últimas horas a partir do histórico gravado pelo app (precisa do app aberto com 'Gravar histórico' ligado): médias, picos, alertas e programas que mais apareceram.",
                    new JsonObject
                    {
                        ["horas"] = new JsonObject { ["type"] = "integer", ["description"] = "Quantas horas para trás (1 a 168). Padrão: 24." }
                    }),
                Ferramenta("varredura_seguranca", "Varredura de segurança e saúde",
                    "Roda a varredura completa: antivírus, firewall, mineradores, sinais de invasão, inicialização, discos, bateria e travamentos dos últimos 14 dias. Leva de 30 s a 2 min.",
                    new JsonObject
                    {
                        ["so_problemas"] = new JsonObject { ["type"] = "boolean", ["description"] = "Mostrar só itens de Atenção e Crítico. Padrão: true." }
                    }),
                Ferramenta("diagnostico_completo", "Diagnóstico completo",
                    "Tudo junto (estado, memória, engasgos, alertas, programas mais pesados e varredura), no mesmo formato do botão 'Pedir ajuda ao Claude' do app. A varredura leva até 2 min.")
            );
        }

        static Dados Ler()
        {
            // Os contadores de "por segundo" precisam de duas leituras para estabilizar
            Coletor.Coletar();
            Thread.Sleep(1200);
            return Coletor.Coletar();
        }

        static string Executar(string nome, JsonObject a)
        {
            switch (nome)
            {
                case "estado_atual": return EstadoAtual(Ler());
                case "processos": return Processos(Ler(), Arg(a, "ordenar_por", "cpu"), ArgBool(a, "agrupar", true), ArgInt(a, "limite", 25, 1, 100));
                case "memoria_detalhada": return Memoria(Ler());
                case "historico": return Historico(ArgInt(a, "horas", 24, 1, 168));
                case "varredura_seguranca": return VarreduraTexto(Varredura.Executar(), ArgBool(a, "so_problemas", true));
                case "diagnostico_completo":
                    var d = Ler();
                    if (string.IsNullOrEmpty(Contexto.Maquina)) Contexto.Maquina = Maquina(d);
                    return Relatorio.TextoClaude(d, Analisador.Alertas(d), Analisador.Engasgos(d), Varredura.Executar());
                default: throw new Exception("Ferramenta desconhecida: " + nome);
            }
        }

        static string Arg(JsonObject a, string k, string padrao) { try { return a[k]?.GetValue<string>() ?? padrao; } catch { return padrao; } }
        static bool ArgBool(JsonObject a, string k, bool padrao) { try { return a[k] == null ? padrao : a[k].GetValue<bool>(); } catch { return padrao; } }
        static int ArgInt(JsonObject a, string k, int padrao, int min, int max)
        {
            try { return a[k] == null ? padrao : Math.Max(min, Math.Min(max, a[k].GetValue<int>())); }
            catch { return padrao; }
        }

        static string Maquina(Dados d) =>
            Txt.F("{0} • {1} • {2:N1} GB de RAM • {3} • Windows {4}", Environment.MachineName, d.CpuNome, d.RamTotalGB, string.Join(" + ", d.Gpus), Environment.OSVersion.Version);

        static string EstadoAtual(Dados d)
        {
            Contexto.Maquina = Maquina(d);
            var eng = Analisador.Engasgos(d);
            var al = Analisador.Alertas(d);
            var sb = new StringBuilder();
            sb.AppendLine("# Estado atual do PC (" + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", Txt.BR) + ")");
            sb.AppendLine(Contexto.Maquina);
            sb.AppendLine();
            sb.AppendLine(Txt.F("- CPU: {0:N0}% (clock {1:N0}% do nominal){2}", d.Cpu, d.DesempenhoPct, d.TempC > 0 ? Txt.F(" • {0} °C", d.TempC) : ""));
            sb.AppendLine(Txt.F("- RAM: {0:N0}% usada ({1:N1} de {2:N1} GB livres) • comprometida {3:N0}%", d.RamPct, d.RamLivreGB, d.RamTotalGB, d.CommitPct));
            sb.AppendLine(Txt.F("- Memória lida do disco: {0:N0} páginas/s", d.PaginasSeg));
            sb.AppendLine(Txt.F("- GPU: {0:N0}%", Math.Max(0, d.Gpu)));
            sb.AppendLine(Txt.F("- Disco: {0:N0}% ocupado • {1:N1} MB/s • fila {2}", d.DiscoPct, d.DiscoMBs, d.DiscoFila));
            foreach (var u in d.Unidades) sb.AppendLine(Txt.F("  - {0} {1:N1} GB livres de {2:N0} GB ({3:N0}%)", u.Letra, u.LivreGB, u.TotalGB, u.LivrePct));
            sb.AppendLine("- Rede: envio " + Txt.Taxa(d.RedeEnvioKBs) + " • recebe " + Txt.Taxa(d.RedeRecebKBs));
            if (d.TemBateria)
                sb.AppendLine(Txt.F("- Bateria: {0}% • {1} • saúde {2}% • {3} ciclos", d.BatPct,
                    d.NaTomada ? "na tomada" : Txt.F("gastando {0:N1} W", d.DescargaW), d.BatSaude, d.BatCiclos));
            sb.AppendLine("- Ligado há " + Txt.Horas(d.LigadoHoras) + Txt.F(" • usuário parado há {0} s", d.OciosoSeg));
            sb.AppendLine();
            sb.AppendLine("## Engasgos");
            sb.AppendLine(eng.Veredito);
            foreach (var l in eng.Linhas.Where(x => Txt.Nivel(x.Severidade) >= 2)) sb.AppendLine($"- [{l.Severidade}] {l.Item}: {l.Valor}");
            sb.AppendLine(eng.Culpados);
            sb.AppendLine();
            sb.AppendLine("## Alertas");
            sb.AppendLine("(Alertas que dependem de vários minutos de observação só aparecem no app aberto.)");
            if (al.Count == 0) sb.AppendLine("- Nenhum agora.");
            foreach (var x in al) sb.AppendLine($"- [{x.Severidade}] {x.Problema}: {x.Detalhe}");
            return sb.ToString();
        }

        static string Processos(Dados d, string ordem, bool agrupar, int limite)
        {
            Func<ProcInfo, double> campo;
            switch ((ordem ?? "").ToLowerInvariant())
            {
                case "gpu": campo = p => p.Gpu; break;
                case "ram": campo = p => p.RamMB; break;
                case "pedindo_memoria": campo = p => p.Faltas; break;
                case "disco": campo = p => p.DiscoKBs; break;
                default: campo = p => p.Cpu; ordem = "cpu"; break;
            }
            IEnumerable<ProcInfo> lista = d.TodosProcs;
            if (agrupar)
                lista = d.TodosProcs.GroupBy(p => p.Nome, StringComparer.OrdinalIgnoreCase).Select(g => new ProcInfo
                {
                    Pid = g.First().Pid,
                    Nome = g.Key,
                    Qtd = g.Count(),
                    Cpu = g.Sum(p => p.Cpu),
                    Gpu = Math.Min(100, g.Sum(p => p.Gpu)),
                    RamMB = g.Sum(p => p.RamMB),
                    DiscoKBs = g.Sum(p => p.DiscoKBs),
                    Faltas = g.Sum(p => p.Faltas),
                    Alerta = string.Join(", ", g.Select(p => p.Alerta).Where(x => !string.IsNullOrEmpty(x)).Distinct()),
                    Caminho = g.Select(p => p.Caminho).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? ""
                });
            var top = lista.Where(p => p.Nome != "Idle").OrderByDescending(campo).Take(limite).ToList();

            var sb = new StringBuilder();
            sb.AppendLine(Txt.F("# Processos ordenados por {0}{1} ({2:HH:mm:ss})", ordem, agrupar ? ", somando por programa" : "", DateTime.Now));
            sb.AppendLine("RAM = memória própria do processo (working set privado). Pede memória/s = falhas de página por segundo.");
            sb.AppendLine();
            sb.AppendLine("| Programa | Qtd | PID | CPU % | GPU % | RAM MB | E/S KB/s | Pede memória/s | Alerta | Caminho |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var p in top)
                sb.AppendLine(Txt.F("| {0} | {1} | {2} | {3:N1} | {4:N0} | {5:N0} | {6:N0} | {7:N0} | {8} | {9} |",
                    p.Nome, p.Qtd, p.Pid, p.Cpu, p.Gpu, p.RamMB, p.DiscoKBs, p.Faltas, p.Alerta, p.Caminho));
            return sb.ToString();
        }

        static string Memoria(Dados d)
        {
            var eng = Analisador.Engasgos(d);
            var sb = new StringBuilder();
            sb.AppendLine("# Memória em detalhe (" + DateTime.Now.ToString("HH:mm:ss", Txt.BR) + ")");
            sb.AppendLine(eng.MemDetalhe);
            sb.AppendLine();
            sb.AppendLine(eng.MemCulpados);
            sb.AppendLine();
            foreach (var l in eng.Linhas.Where(x => x.Item.Contains("Memória") || x.Item.Contains("paginação")))
                sb.AppendLine($"- [{l.Severidade}] {l.Item}: {l.Valor}");
            return sb.ToString();
        }

        static string VarreduraTexto(List<Achado> itens, bool soProblemas)
        {
            var sb = new StringBuilder();
            int crit = itens.Count(x => x.Severidade == "Crítico"), aten = itens.Count(x => x.Severidade == "Atenção");
            sb.AppendLine(Txt.F("# Varredura de segurança e saúde: {0} crítico(s), {1} de atenção, {2} itens verificados", crit, aten, itens.Count));
            if (!Nativo.EhAdmin())
                sb.AppendLine("(Rodou sem administrador: logs de login, exclusões do antivírus e desgaste do SSD ficaram de fora.)");
            var mostrar = soProblemas ? itens.Where(x => Txt.Nivel(x.Severidade) >= 2).ToList() : itens;
            if (mostrar.Count == 0) sb.AppendLine("Nenhum problema encontrado.");
            foreach (var x in mostrar)
            {
                sb.AppendLine($"- [{x.Severidade}] {x.Categoria} — {x.Item}: {x.Detalhe}");
                if (!string.IsNullOrEmpty(x.Recomendacao)) sb.AppendLine("  Sugestão do app: " + x.Recomendacao);
            }
            return sb.ToString();
        }

        static string Historico(int horas)
        {
            var limite = DateTime.Now.AddHours(-horas);
            var linhas = new List<string[]>();
            if (Directory.Exists(Pastas.Dados))
            {
                foreach (var arq in Directory.GetFiles(Pastas.Dados, "historico-*.csv").OrderBy(x => x))
                {
                    try
                    {
                        using (var fs = new FileStream(arq, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var r = new StreamReader(fs, Encoding.UTF8))
                        {
                            string l;
                            bool cab = true;
                            while ((l = r.ReadLine()) != null)
                            {
                                if (cab) { cab = false; continue; }
                                var c = DividirCsv(l);
                                if (c.Length < 16) continue;
                                if (DateTime.TryParseExact(c[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && t >= limite)
                                    linhas.Add(c);
                            }
                        }
                    }
                    catch { }
                }
            }
            if (linhas.Count == 0)
                return "Sem histórico nesse período. O app precisa ficar aberto (pode ser escondido perto do relógio) com a opção 'Gravar histórico' ligada.";

            double N(string s) => double.TryParse(s, NumberStyles.Any, Txt.BR, out var v) ? v : 0;
            var cpu = linhas.Select(c => N(c[1])).ToList();
            var ram = linhas.Select(c => N(c[2])).ToList();
            var gpu = linhas.Select(c => N(c[3])).ToList();
            var disco = linhas.Select(c => N(c[4])).ToList();
            var pag = linhas.Select(c => N(c[11])).ToList();
            var bat = linhas.Where(c => c[7] == "não").Select(c => N(c[9])).Where(v => v > 0).ToList();

            var sb = new StringBuilder();
            sb.AppendLine(Txt.F("# Histórico das últimas {0} h ({1} leituras, de {2} a {3})", horas, linhas.Count, linhas.First()[0], linhas.Last()[0]));
            sb.AppendLine(Txt.F("- CPU: média {0:N0}% • pico {1:N0}%", cpu.Average(), cpu.Max()));
            sb.AppendLine(Txt.F("- RAM: média {0:N0}% • pico {1:N0}% • acima de 90% em {2:N0}% do tempo", ram.Average(), ram.Max(), 100.0 * ram.Count(v => v >= 90) / ram.Count));
            sb.AppendLine(Txt.F("- GPU: média {0:N0}% • pico {1:N0}%", gpu.Where(v => v >= 0).DefaultIfEmpty(0).Average(), gpu.Max()));
            sb.AppendLine(Txt.F("- Disco: média {0:N0}% • pico {1:N0}%", disco.Average(), disco.Max()));
            sb.AppendLine(Txt.F("- Memória lida do disco: média {0:N0} páginas/s • pico {1:N0} • acima de 500/s em {2:N0}% do tempo", pag.Average(), pag.Max(), 100.0 * pag.Count(v => v >= 500) / pag.Count));
            if (bat.Count > 0) sb.AppendLine(Txt.F("- Na bateria: gasto médio {0:N1} W • pico {1:N1} W", bat.Average(), bat.Max()));

            var topCpu = linhas.Select(c => c[13]).Where(s => s != "").GroupBy(s => s).OrderByDescending(g => g.Count()).Take(5)
                .Select(g => Txt.F("{0} ({1:N0}%)", g.Key, 100.0 * g.Count() / linhas.Count));
            sb.AppendLine("- Quem mais apareceu no topo de CPU: " + string.Join(", ", topCpu));
            var topGpu = linhas.Select(c => c[14]).Where(s => s != "").GroupBy(s => s).OrderByDescending(g => g.Count()).Take(5)
                .Select(g => Txt.F("{0} ({1:N0}%)", g.Key, 100.0 * g.Count() / linhas.Count));
            sb.AppendLine("- Quem mais apareceu no topo de GPU: " + string.Join(", ", topGpu));

            var alertas = linhas.SelectMany(c => c[15].Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries))
                .GroupBy(s => s).OrderByDescending(g => g.Count()).Take(10).ToList();
            sb.AppendLine();
            sb.AppendLine("## Alertas mais frequentes (em quantas leituras apareceram)");
            if (alertas.Count == 0) sb.AppendLine("- Nenhum.");
            foreach (var g in alertas) sb.AppendLine(Txt.F("- {0}: {1}× ({2:N0}% do tempo)", g.Key, g.Count(), 100.0 * g.Count() / linhas.Count));
            return sb.ToString();
        }

        static string[] DividirCsv(string linha)
        {
            var campos = new List<string>();
            var atual = new StringBuilder();
            bool aspas = false;
            for (int i = 0; i < linha.Length; i++)
            {
                char ch = linha[i];
                if (aspas)
                {
                    if (ch == '"' && i + 1 < linha.Length && linha[i + 1] == '"') { atual.Append('"'); i++; }
                    else if (ch == '"') aspas = false;
                    else atual.Append(ch);
                }
                else if (ch == '"') aspas = true;
                else if (ch == ';') { campos.Add(atual.ToString()); atual.Clear(); }
                else atual.Append(ch);
            }
            campos.Add(atual.ToString());
            return campos.ToArray();
        }
    }
}
