using System;
using System.Collections.Generic;
using System.Linq;

namespace SaudeNotebook
{
    public class ResultadoEngasgos
    {
        public List<LinhaEngasgo> Linhas = new List<LinhaEngasgo>();
        public string Veredito = "";
        public int Pior;
        public string Culpados = "";
        public string MemDetalhe = "";
        public string MemCulpados = "";
    }

    /// <summary>Transforma as leituras em diagnósticos e alertas.</summary>
    public class Analise
    {
        static readonly HashSet<string> IgnorarTop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Idle", "System", "_Total", "Memory Compression", "Registry", "Secure System", "WmiPrvSE" };

        static readonly HashSet<string> Manutencao = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MsMpEng", "MsMpEngCP", "NisSrv", "MpDefenderCoreService", "SearchIndexer", "SearchProtocolHost", "SearchFilterHost",
            "TiWorker", "TrustedInstaller", "MoUsoCoreWorker", "usocoreworker", "CompatTelRunner", "svchost", "defrag", "OneDrive",
            "sppsvc", "WaaSMedicAgent", "MicrosoftEdgeUpdate", "wuauclt", "dllhost", "backgroundTaskHost", "drvinst"
        };

        readonly Dictionary<string, int> _sus = new Dictionary<string, int>();
        readonly Dictionary<string, List<(DateTime T, double MB)>> _ramHist = new Dictionary<string, List<(DateTime, double)>>();
        public List<Vazamento> Vazamentos = new List<Vazamento>();

        static readonly int MeuPid = Environment.ProcessId;

        int Sustentar(string k, bool cond)
        {
            int v = cond ? (_sus.TryGetValue(k, out var a) ? a : 0) + 1 : 0;
            _sus[k] = v;
            return v;
        }

        public static ProcInfo Top(Dados d, Func<ProcInfo, double> campo)
        {
            return d.Procs.Where(p => p.Pid != MeuPid && !IgnorarTop.Contains(p.Nome))
                          .OrderByDescending(campo).FirstOrDefault();
        }

        /// <summary>Quem mais pede memória e quem mais ocupa RAM, somando processos do mesmo programa.</summary>
        public static (string Faltas, string Ram) CulpadosMemoria(Dados d)
        {
            var todos = (d.TodosProcs.Count > 0 ? d.TodosProcs : d.Procs)
                .Where(p => p.Pid != MeuPid && !IgnorarTop.Contains(p.Nome));
            var grupos = todos.GroupBy(p => p.Nome, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { Nome = g.Key, Qtd = g.Count(), Faltas = g.Sum(p => p.Faltas), Ram = g.Sum(p => p.RamMB) })
                .ToList();
            string Rot(string nome, int qtd) => qtd > 1 ? Txt.F("{0} ×{1}", nome, qtd) : nome;
            var f = grupos.Where(g => g.Faltas > 0).OrderByDescending(g => g.Faltas).Take(3)
                .Select(g => Txt.F("{0} ({1:N0}/s)", Rot(g.Nome, g.Qtd), g.Faltas));
            var m = grupos.OrderByDescending(g => g.Ram).Take(3)
                .Select(g => Txt.F("{0} ({1:N0} MB)", Rot(g.Nome, g.Qtd), g.Ram));
            return (string.Join(", ", f), string.Join(", ", m));
        }

        /// <summary>A cada 30 s guarda a RAM dos processos grandes; acusa quem cresce sem parar por 5 a 10 min.</summary>
        public void RastrearVazamentos(Dados d, int tick)
        {
            if (tick % 10 != 0) return;
            var agora = DateTime.Now;
            var vistos = new HashSet<string>();
            foreach (var p in d.TodosProcs)
            {
                if (p.RamMB < 150) continue;
                string k = p.Pid + "|" + p.Nome;
                vistos.Add(k);
                if (!_ramHist.TryGetValue(k, out var h)) { h = new List<(DateTime, double)>(); _ramHist[k] = h; }
                h.Add((agora, p.RamMB));
                while (h.Count > 21) h.RemoveAt(0);
            }
            foreach (var k in _ramHist.Keys.ToList())
                if (!vistos.Contains(k)) _ramHist.Remove(k);

            var achados = new List<Vazamento>();
            foreach (var kv in _ramHist)
            {
                var h = kv.Value;
                if (h.Count < 11) continue;
                double ini = h[0].MB, fim = h[h.Count - 1].MB;
                if (fim - ini < 400 || fim < ini * 1.3) continue;
                int quedas = 0;
                for (int i = 1; i < h.Count; i++) if (h[i].MB < h[i - 1].MB - 30) quedas++;
                if (quedas > 2) continue;
                var partes = kv.Key.Split('|');
                achados.Add(new Vazamento
                {
                    Pid = int.Parse(partes[0]),
                    Nome = partes[1],
                    De = ini,
                    Para = fim,
                    Min = (h[h.Count - 1].T - h[0].T).TotalMinutes
                });
            }
            Vazamentos = achados;
        }

        public ResultadoEngasgos Engasgos(Dados d)
        {
            var res = new ResultadoEngasgos();
            var L = res.Linhas;
            void Add(string item, string valor, string sev, string exp) =>
                L.Add(new LinhaEngasgo { Item = item, Valor = valor, Severidade = sev, Explicacao = exp });

            Add("Uso da CPU", Txt.F("{0:N0}%", d.Cpu), d.Cpu >= 90 ? "Atenção" : "OK",
                "Acima de 90% por muito tempo, tudo fica lento. Veja quem está usando na aba Processos.");

            if (d.DesempenhoPct > 0)
            {
                bool bateria = d.TemBateria && !d.NaTomada;
                Add("Velocidade da CPU", Txt.F("{0:N0}% do nominal", d.DesempenhoPct),
                    d.DesempenhoPct < 50 && d.Cpu >= 30 ? "Atenção" : "OK",
                    "Abaixo de 50% com a CPU ocupada quer dizer que ela está freando (throttling)." +
                    (bateria ? " Na bateria, o Windows corta o clock de propósito para economizar."
                             : " Na tomada, clock baixo com a CPU ocupada costuma ser calor: limpe as saídas de ar e use o notebook em superfície dura."));
            }

            double lat = d.Dpc + d.Interrupcao;
            Add("Latência de drivers", Txt.F("{0:N1}% (DPC {1:N1} + IRQ {2:N1})", lat, d.Dpc, d.Interrupcao),
                lat >= 10 ? "Crítico" : lat >= 5 ? "Atenção" : "OK",
                "Tempo em que drivers seguram a CPU. Alto causa áudio picotando, mouse travando e quedas de FPS. Suspeitos comuns: Wi-Fi, placa de vídeo e chipset. Atualize os drivers.");

            Add("Disco ocupado", Txt.F("{0:N0}% • fila {1}", d.DiscoPct, d.DiscoFila),
                d.DiscoPct >= 95 || d.DiscoFila >= 4 ? "Atenção" : "OK",
                "Disco a 100% trava janelas e deixa programas lentos para abrir. Veja a coluna E/S na aba Processos.");

            Add("Memória RAM", Txt.F("{0:N0}% usada", d.RamPct),
                d.RamPct >= 90 ? "Crítico" : d.RamPct >= 80 ? "Atenção" : "OK",
                "Com a RAM cheia o Windows usa o disco como memória, que é muito mais lento.");

            Add("Memória comprometida", Txt.F("{0:N0}% ({1:N1} de {2:N1} GB)", d.CommitPct, d.CommitGB, d.CommitLimiteGB),
                d.CommitPct >= 95 ? "Crítico" : d.CommitPct >= 85 ? "Atenção" : "OK",
                "RAM somada ao arquivo de paginação. Perto de 100%, programas fecham sozinhos ou dão erro de memória.");

            var mem = CulpadosMemoria(d);
            string sevPag = d.PaginasSeg >= 2000 ? "Crítico" : d.PaginasSeg >= 500 ? "Atenção" : "OK";
            string exp = "Alto e constante = falta de RAM. Picos curtos ao abrir programas são normais.";
            if (mem.Faltas != "") exp += " Quem mais pede memória agora: " + mem.Faltas + ".";
            if (sevPag != "OK" && mem.Ram != "") exp += " Quem mais ocupa RAM: " + mem.Ram + ".";
            Add("Memória lida do disco", Txt.F("{0:N0} páginas/s", d.PaginasSeg), sevPag, exp);

            if (d.PagTamMB == 0)
            {
                Add("Arquivo de paginação", "desligado", d.CommitPct >= 80 ? "Atenção" : "Info",
                    "Sem ele, quando a RAM acaba os programas fecham na hora. Deixe em Gerenciado pelo sistema: Configurações avançadas do sistema > Desempenho > Avançado > Memória virtual.");
            }
            else if (d.PagTamMB > 0)
            {
                double uso = 100 * d.PagUsoMB / d.PagTamMB;
                Add("Arquivo de paginação", Txt.F("{0:N0}% de {1:N1} GB em uso", uso, d.PagTamMB / 1024), uso >= 80 ? "Atenção" : "OK",
                    "Memória extra guardada no disco. Muito usado = RAM insuficiente para o que está aberto.");
            }

            if (d.TempC > 0)
                Add("Temperatura (sensor ACPI)", Txt.F("{0} °C", d.TempC), d.TempC >= 90 ? "Crítico" : d.TempC >= 80 ? "Atenção" : "OK",
                    "Sensor da placa-mãe, nem sempre é o da CPU. Para CPU e GPU de verdade, use o NitroSense ou o HWiNFO.");
            else
                Add("Temperatura", "n/d", "Info", "O Windows só libera o sensor com administrador, e nem todo notebook expõe ele.");

            if (d.Gpu >= 0)
                Add("Placa de vídeo", Txt.F("{0:N0}%", d.Gpu), d.Gpu >= 95 ? "Info" : "OK",
                    "GPU no máximo é normal em jogo. Fora de jogo, veja quem está usando na aba Processos (coluna GPU).");

            if (d.TemBateria)
            {
                if (d.NaTomada) Add("Energia", "Na tomada", "OK", "Na tomada o notebook roda com desempenho total.");
                else Add("Energia", Txt.F("Bateria, {0:N1} W", d.DescargaW), d.DescargaW >= 20 ? "Atenção" : "OK",
                    "Uso leve costuma gastar de 8 a 17 W. Acima de 20 W, a placa de vídeo dedicada provavelmente está ligada.");
            }

            var menor = d.Unidades.OrderBy(u => u.LivrePct).FirstOrDefault();
            if (menor != null)
                Add("Espaço em disco", Txt.F("{0} {1:N0} GB livres ({2:N0}%)", menor.Letra, menor.LivreGB, menor.LivrePct),
                    menor.LivrePct < 5 ? "Crítico" : menor.LivrePct < 10 ? "Atenção" : "OK",
                    "Com menos de 10% livre o Windows fica lento e não consegue se atualizar. Configurações > Sistema > Armazenamento > Recomendações de limpeza.");

            Add("Tempo sem reiniciar", Txt.Horas(d.LigadoHoras),
                d.LigadoHoras >= 168 ? "Atenção" : d.LigadoHoras >= 72 ? "Info" : "OK",
                "Reiniciar de vez em quando limpa vazamentos de memória. Desligar com Inicialização Rápida ligada não conta.");

            // Veredito
            var prob = L.Where(x => Txt.Nivel(x.Severidade) >= 2).ToList();
            res.Pior = prob.Count == 0 ? 0 : prob.Max(x => Txt.Nivel(x.Severidade));
            res.Veredito = prob.Count == 0
                ? "Nada travando o PC agora."
                : "Possível causa de lentidão: " + string.Join(", ", prob.Select(x => x.Item.ToLowerInvariant())) + ".";

            var c = Top(d, p => p.Cpu); var g = Top(d, p => p.Gpu); var r = Top(d, p => p.RamMB); var io = Top(d, p => p.DiscoKBs);
            res.Culpados = Txt.F("Quem mais consome agora — CPU: {0} ({1:N0}%)   •   GPU: {2} ({3:N0}%)   •   RAM: {4} ({5:N0} MB)   •   E/S: {6} ({7:N0} KB/s)",
                c?.Nome, c?.Cpu, g?.Nome, g?.Gpu, r?.Nome, r?.RamMB, io?.Nome, io?.DiscoKBs);

            // Memória em detalhe
            double usado = d.RamTotalGB - d.RamLivreGB;
            string pag = "arquivo de paginação: n/d";
            if (d.PagTamMB > 0) pag = Txt.F("arquivo de paginação: {0:N0}% de {1:N1} GB (pico {2:N0}%)", 100 * d.PagUsoMB / d.PagTamMB, d.PagTamMB / 1024, 100 * d.PagPicoMB / d.PagTamMB);
            else if (d.PagTamMB == 0) pag = "arquivo de paginação: desligado";
            res.MemDetalhe = Txt.F("Em uso: {0:N1} de {1:N1} GB   •   Comprometida: {2:N1} de {3:N1} GB   •   Cache que o Windows libera se precisar: {4:N1} GB   •   {5}\n" +
                                   "Lendo do disco para a memória: {6:N0} páginas/s ({7:N0} leituras/s)   •   Falhas de página no total: {8:N0}/s",
                usado, d.RamTotalGB, d.CommitGB, d.CommitLimiteGB, d.CacheGB, pag, d.PaginasSeg, d.LeiturasSeg, d.FaltasSeg);

            var linhas = new List<string>();
            if (mem.Faltas != "") linhas.Add("Quem mais pede memória agora (falhas de página/s): " + mem.Faltas);
            if (mem.Ram != "") linhas.Add("Quem mais ocupa RAM: " + mem.Ram);
            if (Vazamentos.Count > 0)
                linhas.Add("Crescendo sem liberar: " + string.Join(", ", Vazamentos.Select(v => Txt.F("{0} (+{1:N0} MB em {2:N0} min)", v.Nome, v.Para - v.De, v.Min))));
            linhas.Add("Falhas de página que viram leitura do disco só aparecem por programa no Monitor de Recursos: aba Memória, coluna Falhas Graves/s.");
            res.MemCulpados = string.Join("\n", linhas);
            return res;
        }

        public List<Alerta> Alertas(Dados d)
        {
            var al = new List<Alerta>();
            void Add(string chave, string sev, string prob, string det) =>
                al.Add(new Alerta { Chave = chave, Severidade = sev, Problema = prob, Detalhe = det });

            bool ocioso = d.OciosoSeg >= 120;
            var topC = Top(d, p => p.Cpu); var topG = Top(d, p => p.Gpu); var topIO = Top(d, p => p.DiscoKBs);
            string nomeC = topC != null ? Txt.F("{0} ({1:N0}%)", topC.Nome, topC.Cpu) : "?";

            foreach (var p in d.Procs.Where(p => (p.Alerta ?? "").Contains("MINERADOR")))
                Add("min_" + p.Nome, "Crítico", "Possível minerador: " + p.Nome,
                    Txt.F("PID {0} • CPU {1:N0}% • GPU {2:N0}% • {3}. Rode a varredura completa na aba Segurança e saúde.", p.Pid, p.Cpu, p.Gpu, p.Caminho));

            bool pesado = d.Cpu >= 40 || d.Gpu >= 40;
            if (Sustentar("ocioso", ocioso && pesado) >= 5)
            {
                var culp = (d.Gpu >= d.Cpu && topG != null && topG.Gpu > 0) ? topG : topC;
                int min = d.OciosoSeg / 60;
                string nome = culp?.Nome ?? "?";
                if (culp != null && Manutencao.Contains(culp.Nome))
                    Add("ocioso", "Info", "Windows fazendo manutenção", Txt.F("Você está parado há {0} min e o Windows aproveitou para rodar {1}. Normal.", min, nome));
                else
                    Add("ocioso", "Crítico", "Uso pesado com você longe do PC",
                        Txt.F("Parado há {0} min, mas a CPU está em {1:N0}% e a GPU em {2:N0}%. Maior consumo: {3}. É o padrão de minerador escondido. Se você não deixou nada rodando (jogo, render, download), investigue.",
                            min, d.Cpu, Math.Max(0, d.Gpu), nome));
            }

            if (Sustentar("upload", ocioso && d.RedeEnvioKBs >= 500) >= 5)
                Add("upload", "Atenção", "Enviando dados com você ausente",
                    Txt.Taxa(d.RedeEnvioKBs) + " de upload há mais de 15 s com o PC parado. Pode ser backup (OneDrive, Google Drive) ou algo enviando seus arquivos. Veja a parte de Rede na varredura.");

            if (Sustentar("cpu", d.Cpu >= 90) >= 5)
                Add("cpu", "Atenção", "CPU no limite", "Acima de 90% há mais de 15 s. Maior consumo: " + nomeC + ".");

            if (d.CommitPct >= 95)
                Add("commit", "Crítico", "Memória quase esgotada",
                    Txt.F("{0:N0}% da memória comprometida. Programas podem fechar sozinhos. Quem mais ocupa: {1}.", d.CommitPct, CulpadosMemoria(d).Ram));
            else if (Sustentar("ram", d.RamPct >= 90) >= 3)
                Add("ram", "Atenção", "RAM cheia",
                    Txt.F("{0:N0}% usada. O PC vai usar o disco como memória e engasgar. Quem mais ocupa: {1}.", d.RamPct, CulpadosMemoria(d).Ram));

            if (Sustentar("disco", d.DiscoPct >= 90) >= 5)
                Add("disco", "Atenção", "Disco a 100%", "Disco saturado há mais de 15 s. Maior uso de E/S: " + (topIO?.Nome ?? "?") + ".");

            if (Sustentar("dpc", d.Dpc + d.Interrupcao >= 8) >= 3)
                Add("dpc", "Atenção", "Driver causando engasgo",
                    Txt.F("Drivers ocupando {0:N1}% da CPU (DPC/IRQ). Atualize os drivers de vídeo, Wi-Fi e chipset.", d.Dpc + d.Interrupcao));

            if (Sustentar("throttle", d.DesempenhoPct > 0 && d.DesempenhoPct < 50 && d.Cpu >= 50) >= 4)
                Add("throttle", "Atenção", "CPU freando (throttling)",
                    Txt.F("Ocupada, mas rodando a {0:N0}% do clock. {1}", d.DesempenhoPct,
                        d.TemBateria && !d.NaTomada ? "Está na bateria: ligue na tomada para ter desempenho total." : "Provável calor: limpe as saídas de ar."));

            if (d.TempC >= 90) Add("temp", "Crítico", "Temperatura muito alta", Txt.F("{0} °C no sensor ACPI.", d.TempC));
            else if (Sustentar("temp", d.TempC >= 80) >= 5) Add("temp", "Atenção", "Esquentando", Txt.F("{0} °C no sensor ACPI há mais de 15 s.", d.TempC));

            if (d.TemBateria && !d.NaTomada)
            {
                if (Sustentar("dreno", d.DescargaW >= 20) >= 4)
                {
                    string culp = (topG != null && topG.Gpu > 5) ? Txt.F("GPU: {0} ({1:N0}%)", topG.Nome, topG.Gpu) : "CPU: " + nomeC;
                    Add("dreno", "Atenção", "Bateria drenando rápido",
                        Txt.F("Gastando {0:N1} W (acaba em ~{1}). Maior consumo: {2}. Acima de 20 W a placa de vídeo dedicada costuma estar ligada.",
                            d.DescargaW, Txt.Minutos(d.BatRestanteWh / d.DescargaW * 60), culp));
                }
                if (d.BatPct >= 0 && d.BatPct <= 10) Add("batbaixa", "Atenção", "Bateria acabando", d.BatPct + "% restantes.");
            }
            else Sustentar("dreno", false);

            foreach (var u in d.Unidades.Where(u => u.LivrePct < 10))
                Add("esp_" + u.Letra, u.LivrePct < 5 ? "Crítico" : "Atenção", "Pouco espaço em " + u.Letra, Txt.F("{0:N1} GB livres ({1:N0}%).", u.LivreGB, u.LivrePct));

            if (d.LigadoHoras >= 168) Add("uptime", "Info", "Muito tempo sem reiniciar", "Ligado há " + Txt.Horas(d.LigadoHoras) + ". Reinicie quando puder.");
            if (d.BatSaude > 0 && d.BatSaude < 60) Add("batsaude", "Atenção", "Bateria bem gasta", "Segura só " + d.BatSaude + "% da carga original.");

            if (Sustentar("paginando", d.PaginasSeg >= 500) >= 5)
            {
                var mm = CulpadosMemoria(d);
                Add("paginando", "Atenção", "Memória lendo muito do disco",
                    Txt.F("{0:N0} páginas/s há mais de 15 s: falta RAM para o que está aberto. Quem mais pede memória: {1}. Quem mais ocupa RAM: {2}.", d.PaginasSeg, mm.Faltas, mm.Ram));
            }

            foreach (var v in Vazamentos)
                Add("vaz_" + v.Nome, "Atenção", "Possível vazamento de memória: " + v.Nome,
                    Txt.F("Cresceu de {0:N0} para {1:N0} MB em {2:N0} min sem liberar (PID {3}). Se continuar subindo, salve o que estiver fazendo e reabra o programa.", v.De, v.Para, v.Min, v.Pid));

            return al.OrderByDescending(a => Txt.Nivel(a.Severidade)).ToList();
        }
    }
}
