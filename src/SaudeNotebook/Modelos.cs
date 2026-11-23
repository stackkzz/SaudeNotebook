using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SaudeNotebook
{
    public static class Pastas
    {
        public static string Dados => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaudeNotebook");
        public static string Relatorios => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Saude do Notebook");
    }

    public static class Txt
    {
        public static readonly CultureInfo BR = new CultureInfo("pt-BR");
        public static string F(string formato, params object[] args) => string.Format(BR, formato, args);

        public static string Taxa(double kbs) => kbs >= 1024 ? F("{0:N1} MB/s", kbs / 1024) : F("{0:N0} KB/s", kbs);

        public static string Minutos(double min)
        {
            if (double.IsNaN(min) || double.IsInfinity(min) || min < 0) return "?";
            if (min >= 60) return F("{0}h{1:00}", Math.Floor(min / 60), Math.Floor(min % 60));
            return F("{0:N0} min", min);
        }

        public static string Horas(double h) => h < 48 ? F("{0:N0} h", h) : F("{0:N1} dias", h / 24);

        public static int Nivel(string sev)
        {
            switch (sev)
            {
                case "Crítico": return 3;
                case "Atenção": return 2;
                case "Info": return 1;
                default: return 0;
            }
        }
    }

    /// <summary>Contexto da máquina, usado nos textos copiados para o Claude.</summary>
    public static class Contexto
    {
        public static string Maquina = "";
    }

    public interface ICopiavel
    {
        string TextoCopia();
    }

    public class Unidade
    {
        public string Letra;
        public double LivreGB;
        public double TotalGB;
        public double LivrePct;
    }

    public class ProcInfo : ICopiavel
    {
        public int Pid { get; set; }
        public string Nome { get; set; } = "";
        public int Qtd { get; set; } = 1;
        public double Cpu { get; set; }
        public double Gpu { get; set; }
        public double RamMB { get; set; }
        public double DiscoKBs { get; set; }
        public double Faltas { get; set; }
        public string Alerta { get; set; } = "";
        public string Caminho { get; set; } = "";
        public string Cmd { get; set; } = "";

        public string TextoCopia()
        {
            return Txt.F("Notebook: {0}\nNo app Saúde do Notebook, o processo \"{1}\" aparece assim agora:\n" +
                         "CPU {2:N1}% • GPU {3:N1}% • RAM {4:N0} MB • E/S {5:N0} KB/s • {6:N0} falhas de página/s{7}\n" +
                         "Caminho: {8}\n{9}\n\nO que é esse processo, ele é seguro e o que eu faço se ele estiver pesando demais?",
                Contexto.Maquina, Nome, Cpu, Gpu, RamMB, DiscoKBs, Faltas,
                Qtd > 1 ? Txt.F(" • {0} processos somados", Qtd) : "",
                string.IsNullOrEmpty(Caminho) ? "desconhecido" : Caminho,
                string.IsNullOrEmpty(Alerta) ? "" : "Marcação do app: " + Alerta);
        }
    }

    public class Alerta : ICopiavel
    {
        public string Chave { get; set; }
        public string Severidade { get; set; }
        public string Problema { get; set; }
        public string Detalhe { get; set; }

        public string TextoCopia()
        {
            return Txt.F("Notebook: {0}\nO app Saúde do Notebook mostrou este alerta agora:\n[{1}] {2}\n{3}\n\n" +
                         "Me explica o que provavelmente está causando isso e como resolver, passo a passo?",
                Contexto.Maquina, Severidade, Problema, Detalhe);
        }
    }

    public class LinhaEngasgo : ICopiavel
    {
        public string Item { get; set; }
        public string Valor { get; set; }
        public string Severidade { get; set; }
        public string Explicacao { get; set; }

        public string TextoCopia()
        {
            return Txt.F("Notebook: {0}\nNo diagnóstico de engasgos do app Saúde do Notebook:\n[{1}] {2}: {3}\nO que o app diz: {4}\n\n" +
                         "Me explica o que isso significa no meu caso e como resolver, passo a passo?",
                Contexto.Maquina, Severidade, Item, Valor, Explicacao);
        }
    }

    public class Achado : ICopiavel
    {
        public string Severidade { get; set; }
        public string Categoria { get; set; }
        public string Item { get; set; }
        public string Detalhe { get; set; }
        public string Recomendacao { get; set; }

        public string TextoCopia()
        {
            return Txt.F("Notebook: {0}\nA varredura do app Saúde do Notebook encontrou:\n[{1}] {2} — {3}\nDetalhe: {4}\nSugestão do app: {5}\n\n" +
                         "Isso é um problema de verdade? Como eu confiro e resolvo, passo a passo?",
                Contexto.Maquina, Severidade, Categoria, Item, Detalhe,
                string.IsNullOrEmpty(Recomendacao) ? "nenhuma" : Recomendacao);
        }
    }

    public class Vazamento
    {
        public int Pid;
        public string Nome;
        public double De;
        public double Para;
        public double Min;
    }

    /// <summary>Uma leitura completa do estado do PC.</summary>
    public class Dados
    {
        public DateTime Hora = DateTime.Now;
        public int Nucleos;
        public string CpuNome = "CPU";
        public double CpuMaxMHz;
        public List<string> Gpus = new List<string>();

        public double Cpu, Dpc, Interrupcao;
        public double DesempenhoPct = -1;

        public double RamPct, RamTotalGB, RamLivreGB, LigadoHoras, PaginasSeg, CommitPct;
        public double CommitGB, CommitLimiteGB, CacheGB, ModificadaGB, FaltasSeg, LeiturasSeg;
        public double PagTamMB = -1, PagUsoMB, PagPicoMB;

        public double DiscoPct, DiscoFila, DiscoMBs;
        public List<Unidade> Unidades = new List<Unidade>();

        public double RedeEnvioKBs, RedeRecebKBs;
        public double Gpu = -1;

        public List<ProcInfo> Procs = new List<ProcInfo>();
        public List<ProcInfo> TodosProcs = new List<ProcInfo>();

        public bool TemBateria;
        public bool NaTomada = true;
        public double DescargaW, CargaW, BatRestanteWh, BatCheiaWh;
        public int BatPct = -1, BatSaude = -1, BatCiclos = -1;

        public double TempC = -1;
        public int OciosoSeg;
        public long ColetaMs;
    }
}
