using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SaudeNotebook
{
    /// <summary>Varredura completa: segurança, minerador, invasão, inicialização, hardware e estabilidade.</summary>
    public static class Varredura
    {
        static readonly Regex Suspeito = new Regex(
            @"\\AppData\\Local\\Temp\\|\\Windows\\Temp\\|\\Users\\Public\\|\\ProgramData\\[^\\]+\.exe|-enc(odedcommand)?\s|\s-e\s+[A-Za-z0-9+/=]{40,}|frombase64string|downloadstring|downloadfile|invoke-expression|\biex\b|mshta|regsvr32.+(http|scrobj)|rundll32.+(javascript|http)|bitsadmin|certutil.+-urlcache|\.vbs\b|\.hta\b|\.jse\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex AcessoRemoto = new Regex(
            "anydesk|teamviewer|rustdesk|ammyy|vnc|screenconnect|splashtop|radmin|atera|remoteutilities|meshagent|supremo|dwagent|logmein|aeroadmin",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly Regex Pools = new Regex(
            @"nanopool|2miners|f2pool|ethermine|hashvault|minexmr|moneroocean|herominers|unmineable|c3pool|supportxmr|nicehash|xmrpool|miningpoolhub|hiveon|flypool|antpool|viabtc|kryptex|zpool\.ca|prohashing|coinhive|crypto-pool|minergate",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static readonly HashSet<string> Sistema = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "svchost.exe", "lsass.exe", "csrss.exe", "winlogon.exe", "services.exe", "smss.exe", "wininit.exe", "taskhostw.exe", "dllhost.exe",
            "conhost.exe", "rundll32.exe", "spoolsv.exe", "explorer.exe", "dwm.exe", "sihost.exe", "fontdrvhost.exe", "ctfmon.exe",
            "searchindexer.exe", "runtimebroker.exe", "drvinst.exe"
        };

        static readonly int[] PortasMineracao = { 3333, 3334, 3335, 5555, 7777, 13333, 14433, 14444, 17777, 45560, 45700, 10128, 20535 };

        const string SemAdmin = "Precisa de administrador. Clique em Reabrir como administrador e rode de novo.";

        public static List<Achado> Executar()
        {
            var a = new List<Achado>();
            void Add(string sev, string cat, string item, string det, string rec = "") =>
                a.Add(new Achado { Severidade = sev, Categoria = cat, Item = item, Detalhe = det ?? "", Recomendacao = rec ?? "" });
            void Tentar(Action acao) { try { acao(); } catch { } }

            bool admin = Nativo.EhAdmin();
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            bool Dentro(string caminho, string pasta) =>
                !string.IsNullOrEmpty(pasta) && caminho.StartsWith(pasta.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

            if (!admin)
                Add("Info", "Geral", "Modo limitado", "Sem administrador ficaram de fora: logs de login, exclusões do antivírus e desgaste do SSD.",
                    "Clique em Reabrir como administrador e rode de novo.");

            // ---------- Antivírus ----------
            Tentar(() =>
            {
                var avs = Wmi.Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
                string nomes = string.Join(", ", avs.Select(x => Wmi.S(x, "displayName")));
                if (avs.Count == 0)
                    Add("Crítico", "Segurança", "Antivírus", "Nenhum antivírus registrado no Windows.", "Ative o Microsoft Defender em Segurança do Windows.");
                else if (avs.Any(x => (((int)Wmi.D(x, "productState") >> 12) & 0xF) == 1))
                    Add("OK", "Segurança", "Antivírus", "Ativo: " + nomes);
                else
                    Add("Crítico", "Segurança", "Antivírus", "Registrado, mas desligado: " + nomes, "Ligue em Segurança do Windows > Proteção contra vírus e ameaças.");
            });

            Tentar(() =>
            {
                var mp = Wmi.Primeiro(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus");
                if (mp == null) return;
                string modo = Wmi.S(mp, "AMRunningMode");
                if (modo != "" && modo != "Normal")
                {
                    Add("Info", "Segurança", "Microsoft Defender", "Modo: " + modo + " (outro antivírus está no comando).");
                    return;
                }
                if (!Wmi.B(mp, "RealTimeProtectionEnabled"))
                    Add("Crítico", "Segurança", "Proteção em tempo real", "Desligada.", "Ligue em Segurança do Windows > Proteção contra vírus e ameaças > Gerenciar configurações.");
                else
                    Add("OK", "Segurança", "Proteção em tempo real", "Ligada.");
                double idade = Wmi.D(mp, "AntivirusSignatureAge");
                if (idade > 3 && idade < 10000)
                    Add("Atenção", "Segurança", "Assinaturas do Defender", Txt.F("Desatualizadas há {0:N0} dias.", idade), "Rode o Windows Update ou clique em Verificar atualizações no Defender.");
                var tamper = Wmi.Bruto(mp, "IsTamperProtected");
                if (tamper is bool t && !t)
                    Add("Atenção", "Segurança", "Proteção contra adulteração", "Desligada: um malware consegue desativar o Defender.",
                        "Ligue em Segurança do Windows > Gerenciar configurações > Proteção contra adulteração.");
            });

            Tentar(() =>
            {
                var limite = DateTime.Now.AddDays(-30);
                var det = Wmi.Query(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpThreatDetection")
                    .Select(x => new { X = x, Quando = Wmi.Data(x, "InitialDetectionTime") ?? DateTime.MinValue })
                    .Where(x => x.Quando > limite).OrderByDescending(x => x.Quando).Take(10).ToList();
                if (det.Count == 0)
                {
                    Add("OK", "Segurança", "Ameaças recentes", "Nenhuma detecção do Defender nos últimos 30 dias.");
                    return;
                }
                var nomes = new Dictionary<string, string>();
                try
                {
                    foreach (var t in Wmi.Query(@"root\Microsoft\Windows\Defender", "SELECT ThreatID, ThreatName FROM MSFT_MpThreat"))
                        nomes[Wmi.S(t, "ThreatID")] = Wmi.S(t, "ThreatName");
                }
                catch { }
                foreach (var x in det)
                {
                    string id = Wmi.S(x.X, "ThreatID");
                    string nm = nomes.TryGetValue(id, out var n) ? n : "ID " + id;
                    string arqs = string.Join("; ", Wmi.Lista(x.X, "Resources").Take(2));
                    if (Wmi.B(x.X, "ActionSuccess"))
                        Add("Atenção", "Segurança", "Ameaça detectada: " + nm, Txt.F("Em {0:dd/MM HH:mm}, já tratada pelo Defender. {1}", x.Quando, arqs), "Confira se não volta. Rode uma verificação completa.");
                    else
                        Add("Crítico", "Segurança", "Ameaça NÃO removida: " + nm, Txt.F("Em {0:dd/MM HH:mm}. {1}", x.Quando, arqs), "Abra Segurança do Windows > Histórico de proteção e remova.");
                }
            });

            Tentar(() =>
            {
                var pref = Wmi.Primeiro(@"root\Microsoft\Windows\Defender", "SELECT ExclusionPath, ExclusionProcess, ExclusionExtension FROM MSFT_MpPreference");
                if (pref == null) return;
                var exc = Wmi.Lista(pref, "ExclusionPath").Concat(Wmi.Lista(pref, "ExclusionProcess")).Concat(Wmi.Lista(pref, "ExclusionExtension"))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (exc.Any(x => x.Contains("N/A")))
                    Add("Info", "Segurança", "Exclusões do Defender", SemAdmin);
                else if (exc.Count > 0)
                    Add("Atenção", "Segurança", "Exclusões do Defender", "Itens que o antivírus ignora: " + string.Join("; ", exc.Take(8)),
                        "Mineradores se escondem assim. Se não reconhece algum, remova em Segurança do Windows > Exclusões.");
                else
                    Add("OK", "Segurança", "Exclusões do Defender", "Nenhuma.");
            });

            // ---------- Firewall, UAC, acesso remoto, contas ----------
            Tentar(() =>
            {
                var perfis = Wmi.Query(@"root\StandardCimv2", "SELECT Name, Enabled FROM MSFT_NetFirewallProfile");
                if (perfis.Count == 0) return;
                var off = perfis.Where(x => (int)Wmi.D(x, "Enabled") == 0).Select(x => Wmi.S(x, "Name")).ToList();
                if (off.Count > 0) Add("Crítico", "Segurança", "Firewall", "Desligado nos perfis: " + string.Join(", ", off), "Ligue em Segurança do Windows > Firewall e proteção de rede.");
                else Add("OK", "Segurança", "Firewall", "Ligado em todos os perfis.");
            });

            Tentar(() =>
            {
                var lua = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 1);
                if (Convert.ToInt32(lua) == 0)
                    Add("Crítico", "Segurança", "Controle de Conta de Usuário (UAC)", "Desligado: qualquer programa vira administrador sem perguntar.",
                        "Ligue em Painel de Controle > Contas de Usuário > Alterar configurações de Controle de Conta.");
                else Add("OK", "Segurança", "Controle de Conta de Usuário (UAC)", "Ligado.");
            });

            Tentar(() =>
            {
                var rdp = Registry.GetValue(@"HKEY_LOCAL_MACHINE\System\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", 1);
                if (Convert.ToInt32(rdp) == 0)
                    Add("Atenção", "Invasão", "Área de Trabalho Remota", "Ligada: dá para acessar este PC pela rede.", "Se você não usa, desligue em Configurações > Sistema > Área de Trabalho Remota.");
                else Add("OK", "Invasão", "Área de Trabalho Remota", "Desligada.");
            });

            Tentar(() =>
            {
                var sb = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null);
                if (sb == null) return;
                if (Convert.ToInt32(sb) == 1) Add("OK", "Segurança", "Secure Boot", "Ligado.");
                else Add("Atenção", "Segurança", "Secure Boot", "Desligado.", "Protege contra malware que carrega antes do Windows. Ligue na BIOS se não usa outro sistema.");
            });

            Tentar(() =>
            {
                foreach (var u in Wmi.Query("SELECT Name, SID, Disabled FROM Win32_UserAccount WHERE LocalAccount=True"))
                    if (Wmi.S(u, "SID").EndsWith("-501") && !Wmi.B(u, "Disabled"))
                        Add("Atenção", "Invasão", "Conta Convidado", "Ativada.", "Desative: ela permite entrar no PC sem senha.");
            });

            Tentar(() =>
            {
                var grupo = Wmi.Primeiro(@"root\cimv2", "SELECT * FROM Win32_Group WHERE LocalAccount=True AND SID='S-1-5-32-544'");
                if (grupo == null) return;
                var membros = new List<string>();
                foreach (ManagementBaseObject m in grupo.GetRelated(null, "Win32_GroupUser", null, null, "PartComponent", "GroupComponent", false, null))
                    membros.Add(Wmi.S(m, "Domain") + "\\" + Wmi.S(m, "Name"));
                if (membros.Count > 0)
                    Add("Info", "Invasão", "Administradores do PC", string.Join(", ", membros), "Se aparecer uma conta que você não criou, é sinal de invasão.");
            });

            Tentar(() =>
            {
                var hosts = File.ReadAllLines(Path.Combine(win, @"System32\drivers\etc\hosts"))
                    .Select(l => l.Trim())
                    .Where(l => l != "" && !l.StartsWith("#") && !Regex.IsMatch(l, @"^(127\.0\.0\.1|::1)\s+localhost$"))
                    .ToList();
                if (hosts.Count == 0) { Add("OK", "Invasão", "Arquivo hosts", "Sem redirecionamentos."); return; }
                bool grave = Regex.IsMatch(string.Join(" ", hosts), "microsoft|windowsupdate|defender|banco|bank|google|facebook|paypal|nubank|itau|bradesco|caixa", RegexOptions.IgnoreCase);
                Add(grave ? "Atenção" : "Info", "Invasão", "Arquivo hosts modificado", string.Join(" | ", hosts.Take(6)),
                    "O hosts redireciona sites. Se não foi você ou um programa que você conhece (Docker, por exemplo), limpe-o.");
            });

            Tentar(() =>
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k == null) return;
                    if (Convert.ToInt32(k.GetValue("ProxyEnable", 0)) == 1)
                        Add("Atenção", "Invasão", "Proxy ativo", "Seu tráfego passa por: " + k.GetValue("ProxyServer", ""), "Se não foi você que configurou, desligue em Configurações > Rede e Internet > Proxy.");
                    var pac = k.GetValue("AutoConfigURL", "")?.ToString();
                    if (!string.IsNullOrEmpty(pac))
                        Add("Atenção", "Invasão", "Proxy automático", "Script de proxy: " + pac, "Se não foi você que configurou, desligue em Configurações > Rede e Internet > Proxy.");
                }
            });

            Tentar(() =>
            {
                DateTime? ultima = null; string id = "";
                foreach (var h in Wmi.Query("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering"))
                {
                    string s = Wmi.S(h, "InstalledOn");
                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ||
                        DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt))
                    {
                        if (ultima == null || dt > ultima) { ultima = dt; id = Wmi.S(h, "HotFixID"); }
                    }
                }
                if (ultima == null) return;
                int dias = (int)(DateTime.Now - ultima.Value).TotalDays;
                if (dias > 45) Add("Atenção", "Segurança", "Windows Update", Txt.F("Última atualização instalada há {0} dias.", dias), "Abra Configurações > Windows Update e atualize.");
                else Add("OK", "Segurança", "Windows Update", Txt.F("Última atualização há {0} dias ({1}).", dias, id));
            });

            // ---------- Logs de segurança (invasão) ----------
            if (admin)
            {
                Tentar(() =>
                {
                    int falhas = 0;
                    try { falhas = Eventos.Ler("Security", $"*[System[(EventID=4625) and {Eventos.Tempo(1)}]]", 500).Count; } catch { }
                    if (falhas >= 20)
                        Add("Atenção", "Invasão", "Tentativas de login com senha errada", falhas + " nas últimas 24 h.",
                            "Alguém pode estar tentando adivinhar sua senha. Use senha forte e desligue a Área de Trabalho Remota se não usa.");
                    else Add("OK", "Invasão", "Tentativas de login com senha errada", falhas + " nas últimas 24 h.");
                });

                Tentar(() =>
                {
                    foreach (var e in Eventos.Ler("Security", $"*[System[(EventID=4720 or EventID=4732 or EventID=1102) and {Eventos.Tempo(30)}]]", 50))
                    {
                        string quando = Txt.F("{0:dd/MM HH:mm}", e.TimeCreated);
                        if (e.Id == 4720) Add("Atenção", "Invasão", "Conta de usuário criada", $"{quando}: conta '{Eventos.Prop(e, 0)}'.", "Se não foi você, apague a conta e troque suas senhas.");
                        else if (e.Id == 4732) Add("Atenção", "Invasão", "Conta adicionada a um grupo", $"{quando}: grupo '{Eventos.Prop(e, 2)}'.", "Confira se alguém virou administrador sem você saber.");
                        else if (e.Id == 1102) Add("Crítico", "Invasão", "Log de segurança apagado", quando + ".", "Invasores apagam o log para esconder rastros. Se não foi você, investigue.");
                    }
                });

                Tentar(() =>
                {
                    var rdp = Eventos.Ler("Security", $"*[System[(EventID=4624) and {Eventos.Tempo(30)}] and EventData[Data[@Name='LogonType']='10']]", 300);
                    if (rdp.Count > 0)
                    {
                        string ips = string.Join(", ", rdp.Select(e => Eventos.Prop(e, 18)).Where(x => x != "").Distinct());
                        Add("Atenção", "Invasão", "Logins remotos (RDP)", $"{rdp.Count} nos últimos 30 dias, de: {ips}",
                            "Se você não acessa este PC remotamente, alguém acessou. Troque a senha e desligue a Área de Trabalho Remota.");
                    }
                });
            }
            else Add("Info", "Invasão", "Logs de login", SemAdmin);

            Tentar(() =>
            {
                foreach (var e in Eventos.Ler("System", $"*[System[(EventID=7045) and {Eventos.Tempo(14)}]]", 30))
                {
                    string img = Eventos.Prop(e, 1);
                    Add(Suspeito.IsMatch(img) ? "Crítico" : "Info", "Invasão", "Serviço novo instalado: " + Eventos.Prop(e, 0),
                        Txt.F("{0:dd/MM HH:mm}: {1}", e.TimeCreated, img), "Instalar programas cria serviços. Se não reconhece este, pesquise o nome.");
                }
            });

            // ---------- Inicialização ----------
            var inic = new List<string>();
            var chaves = new (RegistryKey Raiz, string Caminho)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce")
            };
            foreach (var ch in chaves)
            {
                Tentar(() =>
                {
                    using (var k = ch.Raiz.OpenSubKey(ch.Caminho))
                    {
                        if (k == null) return;
                        foreach (var nome in k.GetValueNames())
                        {
                            if (nome == "") continue;
                            string cmd = k.GetValue(nome)?.ToString() ?? "";
                            inic.Add(nome);
                            if (Suspeito.IsMatch(cmd))
                                Add("Atenção", "Inicialização", "Item suspeito: " + nome, cmd,
                                    "Comando típico de malware (pasta temporária ou script escondido). Se não reconhece, desative no Gerenciador de Tarefas > Aplicativos de inicialização.");
                        }
                    }
                });
            }
            foreach (var pasta in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
            {
                Tentar(() =>
                {
                    foreach (var f in Directory.GetFiles(pasta))
                    {
                        var nome = Path.GetFileName(f);
                        if (nome.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        inic.Add(nome);
                        if (Regex.IsMatch(Path.GetExtension(f), @"^\.(vbs|js|jse|hta|bat|cmd|ps1|wsf)$", RegexOptions.IgnoreCase))
                            Add("Atenção", "Inicialização", "Script na pasta Inicializar: " + nome, f, "Scripts aqui rodam a cada login. Confira se foi você que colocou.");
                    }
                });
            }
            Add(inic.Count > 15 ? "Atenção" : "Info", "Desempenho", "Programas na inicialização: " + inic.Count, string.Join(", ", inic.Take(25)),
                "Quanto mais itens, mais lento o boot. Desative o que não precisa no Gerenciador de Tarefas > Aplicativos de inicialização.");

            Tentar(() =>
            {
                int terceiros = 0;
                foreach (var t in Wmi.Query(@"root\Microsoft\Windows\TaskScheduler", "SELECT TaskName, TaskPath, State, Actions FROM MSFT_ScheduledTask"))
                {
                    if ((int)Wmi.D(t, "State") == 1) continue; // desativada
                    string caminho = Wmi.S(t, "TaskPath");
                    if (!caminho.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) terceiros++;
                    if (!(Wmi.Bruto(t, "Actions") is ManagementBaseObject[] acoes)) continue;
                    foreach (var ac in acoes)
                    {
                        string cmd = (Wmi.S(ac, "Execute") + " " + Wmi.S(ac, "Arguments")).Trim();
                        if (cmd != "" && Suspeito.IsMatch(cmd))
                            Add("Atenção", "Inicialização", "Tarefa agendada suspeita: " + caminho + Wmi.S(t, "TaskName"), cmd,
                                "Malware usa tarefas agendadas para voltar mesmo depois de removido. Se não reconhece, desative no Agendador de Tarefas.");
                    }
                }
                Add("Info", "Inicialização", "Tarefas agendadas de terceiros", terceiros + " ativas, fora as do Windows.");
            });

            // ---------- Processos: mineradores, disfarces, sem assinatura ----------
            var porPid = new Dictionary<int, string>();
            Tentar(() =>
            {
                var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var remotos = new List<string>();
                var naoAssinados = new List<string>();
                int checados = 0;
                foreach (var p in Wmi.Query("SELECT Name, ProcessId, ExecutablePath, CommandLine FROM Win32_Process"))
                {
                    string nome = Wmi.S(p, "Name"), cam = Wmi.S(p, "ExecutablePath"), cmd = Wmi.S(p, "CommandLine");
                    int pid = (int)Wmi.D(p, "ProcessId");
                    porPid[pid] = nome;

                    if (ColetorAoVivo.RxMinerNome.IsMatch(nome) || ColetorAoVivo.RxMinerCmd.IsMatch(cmd))
                        Add("Crítico", "Minerador", $"Possível minerador: {nome} (PID {pid})", (cam + " " + cmd).Trim(),
                            "Encerre o processo, apague o arquivo e rode verificação completa do Defender. Depois confira Inicialização e Tarefas agendadas.");

                    if (Sistema.Contains(nome) && cam != "" && !Dentro(cam, win))
                        Add("Crítico", "Invasão", "Processo disfarçado: " + nome, "Nome de processo do Windows rodando fora da pasta do Windows: " + cam,
                            "Malware usa nomes do sistema para se esconder. Encerre e rode verificação completa.");

                    if (AcessoRemoto.IsMatch(nome) && !remotos.Contains(nome, StringComparer.OrdinalIgnoreCase)) remotos.Add(nome);

                    if (cam != "" && vistos.Add(cam) && !Dentro(cam, win) && !Dentro(cam, pf) && !Dentro(cam, pf86) && checados < 120)
                    {
                        checados++;
                        if (Assinatura.Valida(cam) == false)
                        {
                            if (Suspeito.IsMatch(cam))
                                Add("Atenção", "Processos", "Programa sem assinatura em pasta suspeita: " + nome, cam,
                                    "Executável sem assinatura rodando de pasta temporária ou pública. Pesquise o nome; se não reconhece, encerre e escaneie.");
                            else naoAssinados.Add(nome);
                        }
                    }
                }
                if (naoAssinados.Count > 0)
                    Add("Info", "Processos", "Programas sem assinatura digital: " + naoAssinados.Count,
                        string.Join(", ", naoAssinados.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Take(20)),
                        "Comum em ferramentas de programação e jogos. Só se preocupe com os que você não reconhece.");
                if (remotos.Count > 0)
                    Add("Atenção", "Invasão", "Programa de acesso remoto rodando", string.Join(", ", remotos),
                        "Permite controlar o PC à distância. Se não foi você que instalou ou não está usando agora, desinstale.");
                else Add("OK", "Invasão", "Acesso remoto", "Nenhum programa conhecido de acesso remoto rodando.");
            });

            // ---------- Rede ----------
            Tentar(() =>
            {
                var cons = Wmi.Query(@"root\StandardCimv2", "SELECT LocalAddress, LocalPort, RemoteAddress, RemotePort, State, OwningProcess FROM MSFT_NetTCPConnection");
                string Nome(ManagementObject c) => porPid.TryGetValue((int)Wmi.D(c, "OwningProcess"), out var n) ? n : "PID " + Wmi.S(c, "OwningProcess");

                var estab = cons.Where(c => (int)Wmi.D(c, "State") == 5)
                    .Where(c => !Regex.IsMatch(Wmi.S(c, "RemoteAddress"), @"^(127\.|::1$|0\.0\.0\.0$|::$)")).ToList();
                foreach (var c in estab.Where(c => PortasMineracao.Contains((int)Wmi.D(c, "RemotePort"))))
                    Add("Atenção", "Minerador", "Conexão em porta de mineração: " + Nome(c),
                        Txt.F("{0}:{1} (PID {2})", Wmi.S(c, "RemoteAddress"), Wmi.S(c, "RemotePort"), Wmi.S(c, "OwningProcess")),
                        "Porta típica de pool de mineração. Confira o programa na aba Processos.");
                var resumo = estab.GroupBy(Nome).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} ({g.Count()})");
                Add("Info", "Rede", "Conexões ativas com a internet: " + estab.Count, string.Join(", ", resumo), "Programas que mais conversam com a internet agora.");

                var ignorar = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "svchost.exe", "lsass.exe", "wininit.exe", "services.exe", "spoolsv.exe", "System" };
                var esc = cons.Where(c => (int)Wmi.D(c, "State") == 2 && (Wmi.S(c, "LocalAddress") == "0.0.0.0" || Wmi.S(c, "LocalAddress") == "::") && Wmi.D(c, "OwningProcess") > 4)
                    .Select(c => new { N = Nome(c), P = Wmi.S(c, "LocalPort") })
                    .Where(x => !ignorar.Contains(x.N))
                    .Select(x => x.N + ":" + x.P).Distinct().OrderBy(x => x).ToList();
                if (esc.Count > 0)
                    Add("Info", "Rede", "Programas aceitando conexões de fora", string.Join(", ", esc.Take(20)),
                        "Portas abertas para a rede. Normal para jogos, servidores de desenvolvimento e apps de chamada. Estranhe o que não reconhecer.");
            });

            Tentar(() =>
            {
                var dns = Wmi.Query(@"root\StandardCimv2", "SELECT Entry FROM MSFT_DNSClientCache")
                    .Select(x => Wmi.S(x, "Entry")).Where(e => Pools.IsMatch(e)).Distinct().ToList();
                if (dns.Count > 0)
                    Add("Crítico", "Minerador", "O PC acessou pool de mineração", string.Join(", ", dns),
                        "Algum programa falou com um servidor de mineração há pouco. Veja a aba Processos e rode verificação completa do Defender.");
                else Add("OK", "Minerador", "Pools de mineração", "Nenhum acesso recente a pools conhecidos.");
            });

            // ---------- Hardware ----------
            Tentar(() =>
            {
                foreach (var pd in Wmi.Query(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk"))
                {
                    string nm = Wmi.S(pd, "FriendlyName");
                    int saude = (int)Wmi.D(pd, "HealthStatus");
                    int midia = (int)Wmi.D(pd, "MediaType");
                    string tipo = midia == 4 ? "SSD" : midia == 3 ? "HD" : "disco";
                    if (saude != 0) Add("Crítico", "Hardware", "Disco: " + nm, saude == 1 ? "Status: Atenção" : "Status: Com problema", "Faça backup agora: o disco está reportando problema.");
                    else Add("OK", "Hardware", "Disco: " + nm, "Saudável (" + tipo + ").");

                    if (!admin) continue;
                    try
                    {
                        foreach (ManagementBaseObject rc in pd.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            var wear = Wmi.Bruto(rc, "Wear");
                            if (wear != null)
                            {
                                double w = Convert.ToDouble(wear);
                                Add(w >= 80 ? "Atenção" : "OK", "Hardware", "Desgaste do SSD: " + nm, Txt.F("{0:N0}% da vida útil usada.", w), w >= 80 ? "Planeje a troca e mantenha backup." : "");
                            }
                            double temp = Wmi.D(rc, "Temperature");
                            if (temp >= 70) Add("Atenção", "Hardware", "Temperatura do disco: " + nm, Txt.F("{0:N0} °C", temp), "Disco quente. Verifique a ventilação.");
                            double erros = Wmi.D(rc, "ReadErrorsUncorrected");
                            if (erros > 0) Add("Crítico", "Hardware", "Erros de leitura: " + nm, Txt.F("{0:N0} erros não corrigidos.", erros), "Faça backup já.");
                        }
                    }
                    catch { }
                }
            });

            Tentar(() =>
            {
                foreach (var ld in Wmi.Query("SELECT DeviceID, FreeSpace, Size FROM Win32_LogicalDisk WHERE DriveType=3"))
                {
                    double tam = Wmi.D(ld, "Size"), livre = Wmi.D(ld, "FreeSpace");
                    if (tam <= 0) continue;
                    double pct = Math.Round(100 * livre / tam);
                    string sev = pct < 5 ? "Crítico" : pct < 10 ? "Atenção" : "OK";
                    Add(sev, "Desempenho", "Espaço livre em " + Wmi.S(ld, "DeviceID"), Txt.F("{0:N1} GB livres ({1:N0}%).", livre / 1073741824.0, pct),
                        sev == "OK" ? "" : "Pouco espaço deixa o Windows lento e impede atualizações. Configurações > Sistema > Armazenamento > Recomendações de limpeza.");
                }
            });

            Tentar(() =>
            {
                double fc = Wmi.D(Wmi.Primeiro(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"), "FullChargedCapacity");
                double dc = Wmi.D(Wmi.Primeiro(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData"), "DesignedCapacity");
                if (fc <= 0 || dc <= 0) return;
                double s = Math.Round(100 * fc / dc);
                string cic = "";
                try
                {
                    var c = Wmi.Primeiro(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount");
                    if (c != null) cic = Txt.F(" • {0:N0} ciclos", Wmi.D(c, "CycleCount"));
                }
                catch { }
                string sev = s < 60 ? "Atenção" : s < 80 ? "Info" : "OK";
                Add(sev, "Hardware", "Saúde da bateria", Txt.F("{0:N0}% da capacidade original ({1:N1} de {2:N1} Wh){3}", s, fc / 1000, dc / 1000, cic),
                    s < 60 ? "Bateria bem gasta. Considere trocar." : s < 80 ? "Desgaste normal pelo uso. Ainda não precisa trocar." : "");
            });

            // ---------- Estabilidade (últimos 14 dias) ----------
            string t14 = Eventos.Tempo(14);
            var consultas = new[]
            {
                (X: $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and {t14}]]", Sev: "Atenção", Cat: "Estabilidade", Item: "Desligamentos inesperados",
                    Rec: "O PC apagou ou travou sem desligar direito. Causas comuns: travamento, superaquecimento, bateria zerada."),
                (X: $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001) and {t14}]]", Sev: "Crítico", Cat: "Estabilidade", Item: "Telas azuis (BSOD)",
                    Rec: "Atualize drivers (vídeo, Wi-Fi, chipset) e a BIOS. Se continuar, teste a RAM com o Diagnóstico de Memória do Windows."),
                (X: $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and {t14}]]", Sev: "Atenção", Cat: "Hardware", Item: "Erros de hardware (WHEA)",
                    Rec: "Processador, RAM ou barramento reportou erro. Se repetir, pode ser calor, overclock ou peça com defeito."),
                (X: $"*[System[Provider[@Name='Display'] and (EventID=4101) and {t14}]]", Sev: "Atenção", Cat: "Estabilidade", Item: "Driver de vídeo travou e reiniciou",
                    Rec: "Causa engasgos e tela preta rápida. Atualize o driver da placa de vídeo. Se você acabou de instalar um driver, ignore os eventos de hoje."),
                (X: $"*[System[(Provider[@Name='disk'] or Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs'] or Provider[@Name='stornvme'] or Provider[@Name='storahci']) and (Level=1 or Level=2) and {t14}]]",
                    Sev: "Atenção", Cat: "Hardware", Item: "Erros de disco", Rec: "Erros de leitura ou escrita. Faça backup e rode chkdsk.")
            };
            foreach (var q in consultas)
            {
                try
                {
                    var ev = Eventos.Ler("System", q.X, 100);
                    if (ev.Count > 0)
                        Add(q.Sev, q.Cat, $"{q.Item}: {ev.Count}", Txt.F("Nos últimos 14 dias. Mais recente: {0:dd/MM HH:mm}.", ev[0].TimeCreated), q.Rec);
                    else Add("OK", q.Cat, q.Item, "Nenhum nos últimos 14 dias.");
                }
                catch { Add("Info", q.Cat, q.Item, "Não foi possível consultar o log."); }
            }

            // ---------- Desempenho geral ----------
            Tentar(() =>
            {
                double h = Environment.TickCount64 / 3600000.0;
                if (h > 168) Add("Atenção", "Desempenho", "Tempo sem reiniciar", Txt.F("{0:N0} dias.", h / 24),
                    "Reiniciar limpa a memória e aplica atualizações. Desligar com Inicialização Rápida ligada não conta como reiniciar.");
                else Add("OK", "Desempenho", "Tempo sem reiniciar", Txt.F("{0:N1} dias.", h / 24));
                var os = Wmi.Primeiro(@"root\cimv2", "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                Add("Info", "Desempenho", "Memória RAM", Txt.F("{0:N1} GB instalados.", Wmi.D(os, "TotalVisibleMemorySize") / 1048576.0));
            });

            Tentar(() =>
            {
                var psi = new ProcessStartInfo("powercfg.exe", "/getactivescheme")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string saida = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    var m = Regex.Match(saida, @"\(([^)]+)\)");
                    if (m.Success) Add("Info", "Desempenho", "Plano de energia", m.Groups[1].Value, "Na bateria, prefira o modo equilibrado ou economia. Na tomada, desempenho.");
                }
            });

            return a.OrderByDescending(x => Txt.Nivel(x.Severidade)).ThenBy(x => x.Categoria).ToList();
        }
    }
}
