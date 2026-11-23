using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace SaudeNotebook
{
    /// <summary>Consultas WMI com tratamento de erro.</summary>
    public static class Wmi
    {
        public static List<ManagementObject> Query(string ns, string wql)
        {
            var lista = new List<ManagementObject>();
            using (var s = new ManagementObjectSearcher(new ManagementScope(ns), new ObjectQuery(wql)))
            using (var col = s.Get())
            {
                foreach (ManagementBaseObject o in col)
                {
                    if (o is ManagementObject mo) lista.Add(mo);
                }
            }
            return lista;
        }

        public static List<ManagementObject> Query(string wql) => Query(@"root\cimv2", wql);

        public static ManagementObject Primeiro(string ns, string wql) => Query(ns, wql).FirstOrDefault();

        public static double D(ManagementBaseObject o, string prop)
        {
            try
            {
                var v = o[prop];
                if (v == null) return 0;
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch { return 0; }
        }

        public static string S(ManagementBaseObject o, string prop)
        {
            try { return o[prop]?.ToString() ?? ""; }
            catch { return ""; }
        }

        public static bool B(ManagementBaseObject o, string prop)
        {
            try
            {
                var v = o[prop];
                return v != null && Convert.ToBoolean(v, CultureInfo.InvariantCulture);
            }
            catch { return false; }
        }

        public static object Bruto(ManagementBaseObject o, string prop)
        {
            try { return o[prop]; }
            catch { return null; }
        }

        public static string[] Lista(ManagementBaseObject o, string prop)
        {
            try
            {
                if (o[prop] is string[] arr) return arr;
                var v = o[prop];
                return v == null ? new string[0] : new[] { v.ToString() };
            }
            catch { return new string[0]; }
        }

        public static DateTime? Data(ManagementBaseObject o, string prop)
        {
            try
            {
                var s = o[prop]?.ToString();
                if (string.IsNullOrEmpty(s)) return null;
                return ManagementDateTimeConverter.ToDateTime(s);
            }
            catch { return null; }
        }
    }

    public static class Eventos
    {
        /// <summary>Lê eventos do log, do mais recente para o mais antigo.</summary>
        public static List<EventRecord> Ler(string log, string xpath, int max)
        {
            var lista = new List<EventRecord>();
            var q = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using (var r = new EventLogReader(q))
            {
                EventRecord e;
                while (lista.Count < max && (e = r.ReadEvent()) != null) lista.Add(e);
            }
            return lista;
        }

        public static string Tempo(int dias) => $"TimeCreated[timediff(@SystemTime) <= {(long)dias * 86400000L}]";

        public static string Prop(EventRecord e, int i)
        {
            try { return e.Properties.Count > i ? e.Properties[i].Value?.ToString() ?? "" : ""; }
            catch { return ""; }
        }
    }

    public static class Nativo
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        public static int SegundosOcioso()
        {
            try
            {
                var l = new LASTINPUTINFO();
                l.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref l)) return 0;
                return (int)(unchecked((uint)Environment.TickCount - l.dwTime) / 1000);
            }
            catch { return 0; }
        }

        public static bool EhAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Verifica a assinatura digital (Authenticode) de um executável.</summary>
    public static class Assinatura
    {
        static readonly Guid AcaoVerificarV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        /// <summary>true = assinado e válido; false = sem assinatura ou inválido; null = não deu para verificar.</summary>
        public static bool? Valida(string caminho)
        {
            if (string.IsNullOrEmpty(caminho) || !File.Exists(caminho)) return null;
            IntPtr pArquivo = IntPtr.Zero, pDados = IntPtr.Zero;
            try
            {
                var arq = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                    pcwszFilePath = caminho
                };
                pArquivo = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
                Marshal.StructureToPtr(arq, pArquivo, false);

                var dados = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2,          // sem interface
                    fdwRevocationChecks = 0,
                    dwUnionChoice = 1,       // arquivo
                    pFile = pArquivo,
                    dwStateAction = 1,       // verificar
                    dwProvFlags = 0x10       // sem checagem de revogação (não acessa a internet)
                };
                pDados = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_DATA)));
                Marshal.StructureToPtr(dados, pDados, false);

                int r = WinVerifyTrust(IntPtr.Zero, AcaoVerificarV2, pDados);

                dados = Marshal.PtrToStructure<WINTRUST_DATA>(pDados);
                dados.dwStateAction = 2;     // fechar
                Marshal.StructureToPtr(dados, pDados, true);
                WinVerifyTrust(IntPtr.Zero, AcaoVerificarV2, pDados);

                return r == 0;
            }
            catch { return null; }
            finally
            {
                if (pArquivo != IntPtr.Zero)
                {
                    Marshal.DestroyStructure(pArquivo, typeof(WINTRUST_FILE_INFO));
                    Marshal.FreeHGlobal(pArquivo);
                }
                if (pDados != IntPtr.Zero) Marshal.FreeHGlobal(pDados);
            }
        }
    }

    public static class Area
    {
        /// <summary>Copia texto para a área de transferência (tenta de novo se ela estiver ocupada).</summary>
        public static bool Copiar(string texto)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(texto ?? ""); return true; }
                catch { Thread.Sleep(60); }
            }
            return false;
        }
    }

    public class Config
    {
        public bool Notificar { get; set; } = true;
        public bool Bandeja { get; set; } = true;
        public bool GravarHist { get; set; } = true;
        public bool NaoPerguntarInstalar { get; set; }
        public string VersaoIgnorada { get; set; } = "";

        static string Arquivo => Path.Combine(Pastas.Dados, "config.json");

        static Config _atual;
        public static Config Atual => _atual ?? (_atual = Carregar());

        static Config Carregar()
        {
            try
            {
                if (File.Exists(Arquivo))
                    return JsonSerializer.Deserialize<Config>(File.ReadAllText(Arquivo)) ?? new Config();
            }
            catch { }
            return new Config();
        }

        public void Salvar()
        {
            try
            {
                Directory.CreateDirectory(Pastas.Dados);
                File.WriteAllText(Arquivo, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>Abrir junto com o Windows (chave Run do usuário).</summary>
    public static class Inicializacao
    {
        const string Chave = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Nome = "SaudeNotebook";

        public static bool Ativo
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(Chave))
                        return k?.GetValue(Nome) != null;
                }
                catch { return false; }
            }
        }

        public static void Definir(bool ligar)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Chave))
            {
                if (ligar) k.SetValue(Nome, $"\"{Environment.ProcessPath}\" --minimizado");
                else k.DeleteValue(Nome, false);
            }
        }

        /// <summary>Remove o atalho da versão em PowerShell e liga a inicialização do app novo no lugar.</summary>
        public static void MigrarVersaoAntiga()
        {
            try
            {
                var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Saude do Notebook.lnk");
                if (File.Exists(lnk))
                {
                    File.Delete(lnk);
                    Definir(true);
                }
                else if (Ativo)
                {
                    Definir(true); // atualiza o caminho do .exe
                }
            }
            catch { }
        }
    }

    public static class Abrir
    {
        public static void Url(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        public static void Programa(string exe, string args = "")
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); } catch { }
        }
    }
}
