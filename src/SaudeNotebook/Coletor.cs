using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace SaudeNotebook
{
    /// <summary>Coleta ao vivo (roda a cada 3 s em segundo plano).</summary>
    public class ColetorAoVivo
    {
        public static readonly Regex RxMinerNome = new Regex(
            "xmrig|nbminer|t-rex|lolminer|phoenixminer|ethminer|gminer|nanominer|cpuminer|ccminer|teamredminer|bminer|srbminer|wildrig|claymore|excavator|minerd|cgminer|bfgminer|kawpowminer|nicehash",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static readonly Regex RxMinerCmd = new Regex(
            @"stratum\+(tcp|ssl|tls)://|--donate-level|--randomx|cryptonight|\bkawpow\b|\bethash\b|\bautolykos\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static readonly Regex RxPastaTemp = new Regex(
            @"\\AppData\\Local\\Temp\\|\\Windows\\Temp\\|\\Users\\Public\\",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex RxGpu = new Regex(
            @"pid_(\d+)_luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_.*engtype_(.+)$", RegexOptions.Compiled);

        static readonly Regex RxSufixo = new Regex(@"#\d+$", RegexOptions.Compiled);

        bool _pronto;
        int _ciclos;
        bool _semTemp;
        int _nucleos = 1;
        string _cpuNome = "CPU";
        double _cpuMaxMHz;
        List<string> _gpus = new List<string>();
        double _batProjeto;
        int _batCiclos = -1;
        string _pentesTexto = "";
        int _slotsLivres = -1;
        Dictionary<int, (string Caminho, string Cmd)> _caminhos = new Dictionary<int, (string, string)>();

        void Iniciar()
        {
            _nucleos = Math.Max(1, Environment.ProcessorCount);
            try
            {
                var c = Wmi.Primeiro(@"root\cimv2", "SELECT Name, MaxClockSpeed FROM Win32_Processor");
                if (c != null)
                {
                    _cpuNome = Wmi.S(c, "Name").Trim();
                    _cpuMaxMHz = Wmi.D(c, "MaxClockSpeed");
                }
            }
            catch { }
            try { _gpus = Wmi.Query("SELECT Name FROM Win32_VideoController").Select(g => Wmi.S(g, "Name")).Where(n => n != "").ToList(); } catch { }
            try { _batProjeto = Wmi.D(Wmi.Primeiro(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData"), "DesignedCapacity"); } catch { }
            try
            {
                var bc = Wmi.Primeiro(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount");
                if (bc != null) _batCiclos = (int)Wmi.D(bc, "CycleCount");
            }
            catch { }
            try
            {
                var mods = Wmi.Query("SELECT Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
                if (mods.Count > 0)
                {
                    var tamanhos = mods.Select(m => Math.Round(Wmi.D(m, "Capacity") / 1073741824.0)).ToList();
                    double mhz = mods.Max(m => Math.Max(Wmi.D(m, "ConfiguredClockSpeed"), Wmi.D(m, "Speed")));
                    int slots = 0;
                    try { slots = Wmi.Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => (int)Wmi.D(a, "MemoryDevices")); } catch { }
                    string pentes = string.Join(" + ", tamanhos.Select(t => Txt.F("{0:N0} GB", t)));
                    _pentesTexto = Txt.F("{0} a {1:N0} MT/s", pentes, mhz);
                    if (slots >= mods.Count)
                    {
                        _slotsLivres = slots - mods.Count;
                        _pentesTexto += Txt.F(" ({0} de {1} slots usados)", mods.Count, slots);
                    }
                }
            }
            catch { }
            _pronto = true;
        }

        public Dados Coletar()
        {
            var sw = Stopwatch.StartNew();
            if (!_pronto) Iniciar();
            _ciclos++;
            if (_ciclos % 100 == 0) _caminhos.Clear(); // PIDs são reutilizados

            var r = new Dados
            {
                Nucleos = _nucleos,
                CpuNome = _cpuNome,
                CpuMaxMHz = _cpuMaxMHz,
                Gpus = _gpus,
                PentesTexto = _pentesTexto,
                SlotsLivres = _slotsLivres
            };

            // ---------- CPU ----------
            try
            {
                var p = Wmi.Primeiro(@"root\cimv2", "SELECT PercentProcessorTime, PercentDPCTime, PercentInterruptTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
                if (p != null)
                {
                    r.Cpu = Wmi.D(p, "PercentProcessorTime");
                    r.Dpc = Wmi.D(p, "PercentDPCTime");
                    r.Interrupcao = Wmi.D(p, "PercentInterruptTime");
                }
            }
            catch { }
            try
            {
                var pi = Wmi.Primeiro(@"root\cimv2", "SELECT PercentProcessorPerformance, PercentofMaximumFrequency FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
                if (pi != null)
                {
                    r.DesempenhoPct = Wmi.D(pi, "PercentProcessorPerformance");
                    if (r.DesempenhoPct <= 0) r.DesempenhoPct = Wmi.D(pi, "PercentofMaximumFrequency");
                }
            }
            catch { }

            // ---------- Memória ----------
            try
            {
                var os = Wmi.Primeiro(@"root\cimv2", "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                if (os != null)
                {
                    double total = Wmi.D(os, "TotalVisibleMemorySize"), livre = Wmi.D(os, "FreePhysicalMemory");
                    r.RamTotalGB = Math.Round(total / 1048576.0, 1);
                    r.RamLivreGB = Math.Round(livre / 1048576.0, 1);
                    if (total > 0) r.RamPct = Math.Round(100 * (1 - livre / total), 1);
                }
                r.LigadoHoras = Math.Round(Environment.TickCount64 / 3600000.0, 1);

                var m = Wmi.Primeiro(@"root\cimv2", "SELECT PagesInputPersec, PercentCommittedBytesInUse, CommittedBytes, CommitLimit, StandbyCacheCoreBytes, StandbyCacheNormalPriorityBytes, StandbyCacheReserveBytes, ModifiedPageListBytes, PageFaultsPersec, PageReadsPersec, PoolNonpagedBytes, PoolPagedBytes FROM Win32_PerfFormattedData_PerfOS_Memory");
                if (m != null)
                {
                    r.PaginasSeg = Wmi.D(m, "PagesInputPersec");
                    r.CommitPct = Wmi.D(m, "PercentCommittedBytesInUse");
                    r.CommitGB = Math.Round(Wmi.D(m, "CommittedBytes") / 1073741824.0, 1);
                    r.CommitLimiteGB = Math.Round(Wmi.D(m, "CommitLimit") / 1073741824.0, 1);
                    r.CacheGB = Math.Round((Wmi.D(m, "StandbyCacheCoreBytes") + Wmi.D(m, "StandbyCacheNormalPriorityBytes") + Wmi.D(m, "StandbyCacheReserveBytes")) / 1073741824.0, 1);
                    r.ModificadaGB = Math.Round(Wmi.D(m, "ModifiedPageListBytes") / 1073741824.0, 2);
                    r.FaltasSeg = Wmi.D(m, "PageFaultsPersec");
                    r.LeiturasSeg = Wmi.D(m, "PageReadsPersec");
                    r.PoolNaoPagMB = Math.Round(Wmi.D(m, "PoolNonpagedBytes") / 1048576.0);
                    r.PoolPagMB = Math.Round(Wmi.D(m, "PoolPagedBytes") / 1048576.0);
                }
            }
            catch { }

            // ---------- Arquivo de paginação ----------
            try
            {
                var pfs = Wmi.Query("SELECT AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage");
                if (pfs.Count > 0)
                {
                    r.PagTamMB = pfs.Sum(x => Wmi.D(x, "AllocatedBaseSize"));
                    r.PagUsoMB = pfs.Sum(x => Wmi.D(x, "CurrentUsage"));
                    r.PagPicoMB = pfs.Sum(x => Wmi.D(x, "PeakUsage"));
                }
                else r.PagTamMB = 0;
            }
            catch { }

            // ---------- Disco ----------
            try
            {
                var dk = Wmi.Primeiro(@"root\cimv2", "SELECT PercentIdleTime, CurrentDiskQueueLength, DiskBytesPersec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'");
                if (dk != null)
                {
                    r.DiscoPct = Math.Max(0, Math.Min(100, 100 - Wmi.D(dk, "PercentIdleTime")));
                    r.DiscoFila = Wmi.D(dk, "CurrentDiskQueueLength");
                    r.DiscoMBs = Math.Round(Wmi.D(dk, "DiskBytesPersec") / 1048576.0, 1);
                }
            }
            catch { }
            try
            {
                foreach (var ld in Wmi.Query("SELECT DeviceID, FreeSpace, Size FROM Win32_LogicalDisk WHERE DriveType=3"))
                {
                    double tam = Wmi.D(ld, "Size"), livre = Wmi.D(ld, "FreeSpace");
                    if (tam <= 0) continue;
                    r.Unidades.Add(new Unidade
                    {
                        Letra = Wmi.S(ld, "DeviceID"),
                        LivreGB = Math.Round(livre / 1073741824.0, 1),
                        TotalGB = Math.Round(tam / 1073741824.0, 1),
                        LivrePct = Math.Round(100 * livre / tam, 1)
                    });
                }
            }
            catch { }

            // ---------- Rede ----------
            try
            {
                double env = 0, rec = 0;
                foreach (var n in Wmi.Query("SELECT BytesSentPersec, BytesReceivedPersec FROM Win32_PerfFormattedData_Tcpip_NetworkInterface"))
                {
                    env += Wmi.D(n, "BytesSentPersec");
                    rec += Wmi.D(n, "BytesReceivedPersec");
                }
                r.RedeEnvioKBs = Math.Round(env / 1024, 1);
                r.RedeRecebKBs = Math.Round(rec / 1024, 1);
            }
            catch { }

            // ---------- GPU ----------
            var gpuPorPid = new Dictionary<int, double>();
            try
            {
                var porTipo = new Dictionary<string, double>();
                var pidTipo = new Dictionary<string, double>();
                foreach (var e in Wmi.Query("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
                {
                    var mt = RxGpu.Match(Wmi.S(e, "Name"));
                    if (!mt.Success) continue;
                    double u = Wmi.D(e, "UtilizationPercentage");
                    if (u <= 0) continue;
                    string k = mt.Groups[2].Value + "|" + mt.Groups[3].Value;
                    porTipo[k] = (porTipo.TryGetValue(k, out var a) ? a : 0) + u;
                    string kp = mt.Groups[1].Value + "|" + k;
                    pidTipo[kp] = (pidTipo.TryGetValue(kp, out var b) ? b : 0) + u;
                }
                r.Gpu = Math.Min(100, Math.Round(porTipo.Count > 0 ? porTipo.Values.Max() : 0, 1));
                foreach (var kv in pidTipo)
                {
                    int pid = int.Parse(kv.Key.Split('|')[0]);
                    double v = Math.Min(100, kv.Value);
                    if (!gpuPorPid.TryGetValue(pid, out var atual) || atual < v) gpuPorPid[pid] = v;
                }
            }
            catch { r.Gpu = -1; }

            // ---------- Processos ----------
            try
            {
                var lista = new List<ProcInfo>();
                foreach (var x in Wmi.Query("SELECT Name, IDProcess, PercentProcessorTime, WorkingSetPrivate, WorkingSet, IODataBytesPersec, PageFaultsPersec FROM Win32_PerfFormattedData_PerfProc_Process"))
                {
                    string nome = Wmi.S(x, "Name");
                    if (nome == "_Total" || nome == "Idle") continue;
                    if (nome == "Memory Compression") r.CompactadaMB = Math.Round(Wmi.D(x, "WorkingSet") / 1048576.0);
                    int pid = (int)Wmi.D(x, "IDProcess");
                    if (pid == 0) continue;
                    lista.Add(new ProcInfo
                    {
                        Pid = pid,
                        Nome = RxSufixo.Replace(nome, ""),
                        Cpu = Math.Round(Wmi.D(x, "PercentProcessorTime") / _nucleos, 1),
                        Gpu = Math.Round(gpuPorPid.TryGetValue(pid, out var g) ? g : 0, 1),
                        RamMB = Math.Round(Wmi.D(x, "WorkingSetPrivate") / 1048576.0, 0),
                        DiscoKBs = Math.Round(Wmi.D(x, "IODataBytesPersec") / 1024, 0),
                        Faltas = Math.Round(Wmi.D(x, "PageFaultsPersec"), 0)
                    });
                }

                var top = lista
                    .OrderByDescending(p => p.Cpu * 3 + p.Gpu * 2 + p.RamMB / 200 + p.DiscoKBs / 500 + p.Faltas / 300)
                    .Take(60).ToList();

                var faltam = top.Where(p => !_caminhos.ContainsKey(p.Pid)).Select(p => p.Pid).ToList();
                if (faltam.Count > 0)
                {
                    string filtro = string.Join(" OR ", faltam.Select(p => "ProcessId=" + p));
                    try
                    {
                        foreach (var w in Wmi.Query("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE " + filtro))
                            _caminhos[(int)Wmi.D(w, "ProcessId")] = (Wmi.S(w, "ExecutablePath"), Wmi.S(w, "CommandLine"));
                    }
                    catch { }
                    foreach (var f in faltam)
                        if (!_caminhos.ContainsKey(f)) _caminhos[f] = ("", "");
                }

                foreach (var p in lista)
                {
                    if (!_caminhos.TryGetValue(p.Pid, out var info)) continue;
                    p.Caminho = info.Caminho;
                    p.Cmd = info.Cmd;
                    var marcas = new List<string>();
                    if (RxMinerNome.IsMatch(p.Nome) || RxMinerCmd.IsMatch(info.Cmd ?? "")) marcas.Add("MINERADOR?");
                    if (RxPastaTemp.IsMatch(info.Caminho ?? "")) marcas.Add("pasta temporária");
                    p.Alerta = string.Join(", ", marcas);
                }

                r.Procs = top;
                r.TodosProcs = lista;
                r.SomaProgramasGB = Math.Round(lista.Where(p => p.Nome != "Memory Compression").Sum(p => p.RamMB) / 1024.0, 1);
            }
            catch { }

            // ---------- Bateria ----------
            try
            {
                var bs = Wmi.Primeiro(@"root\wmi", "SELECT PowerOnline, DischargeRate, ChargeRate, RemainingCapacity FROM BatteryStatus");
                if (bs != null)
                {
                    r.TemBateria = true;
                    r.NaTomada = Wmi.B(bs, "PowerOnline");
                    r.DescargaW = Math.Round(Wmi.D(bs, "DischargeRate") / 1000, 1);
                    r.CargaW = Math.Round(Wmi.D(bs, "ChargeRate") / 1000, 1);
                    double restante = Wmi.D(bs, "RemainingCapacity");
                    r.BatRestanteWh = Math.Round(restante / 1000, 1);
                    double cheia = Wmi.D(Wmi.Primeiro(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"), "FullChargedCapacity");
                    r.BatCheiaWh = Math.Round(cheia / 1000, 1);
                    if (cheia > 0) r.BatPct = (int)Math.Min(100, Math.Round(100 * restante / cheia));
                    if (_batProjeto > 0 && cheia > 0) r.BatSaude = (int)Math.Round(100 * cheia / _batProjeto);
                    r.BatCiclos = _batCiclos;
                }
            }
            catch
            {
                try
                {
                    var wb = Wmi.Primeiro(@"root\cimv2", "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
                    if (wb != null)
                    {
                        r.TemBateria = true;
                        r.BatPct = (int)Wmi.D(wb, "EstimatedChargeRemaining");
                        r.NaTomada = Wmi.D(wb, "BatteryStatus") == 2;
                    }
                }
                catch { }
            }

            // ---------- Temperatura (sensor ACPI, precisa de administrador) ----------
            if (!_semTemp)
            {
                try
                {
                    double mx = -1;
                    foreach (var z in Wmi.Query(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                    {
                        double c = Wmi.D(z, "CurrentTemperature") / 10 - 273.15;
                        if (c > mx && c < 130) mx = c;
                    }
                    if (mx > 0) r.TempC = Math.Round(mx);
                }
                catch { _semTemp = true; }
            }

            r.OciosoSeg = Nativo.SegundosOcioso();
            r.ColetaMs = sw.ElapsedMilliseconds;
            return r;
        }
    }
}
