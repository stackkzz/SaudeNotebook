using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace SaudeNotebook
{
    /// <summary>Copia o app para a pasta de programas do usuário e cria atalhos.</summary>
    public static class Instalador
    {
        public static string Pasta => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SaudeNotebook");
        public static string Exe => Path.Combine(Pasta, "SaudeNotebook.exe");

        static string AtalhoMenu => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Saúde do Notebook.lnk");
        static string AtalhoDesktop => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Saúde do Notebook.lnk");

        public static bool EstaInstalado
        {
            get
            {
                try { return string.Equals(Path.GetFullPath(Environment.ProcessPath), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }
        }

        /// <summary>Pergunta se quer instalar. Retorna true se instalou e abriu a cópia instalada (esta instância deve fechar).</summary>
        public static bool OferecerInstalacao()
        {
            if (EstaInstalado || Config.Atual.NaoPerguntarInstalar) return false;

            bool jaExiste = File.Exists(Exe);
            string msg = jaExiste
                ? "Já existe um Saúde do Notebook instalado neste PC.\n\nSubstituir pela versão que você abriu agora?\n\nSim: substituir e abrir\nNão: só abrir esta cópia desta vez"
                : "Instalar o Saúde do Notebook neste PC?\n\nEle vai para a sua pasta de programas, ganha atalho no Menu Iniciar e na Área de Trabalho e passa a se atualizar sozinho.\n\nSim: instalar agora\nNão: só abrir desta vez";
            var r = MessageBox.Show(msg, "Saúde do Notebook", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
            {
                if (!jaExiste)
                {
                    Config.Atual.NaoPerguntarInstalar = true;
                    Config.Atual.Salvar();
                }
                return false;
            }

            try
            {
                Directory.CreateDirectory(Pasta);
                File.Copy(Environment.ProcessPath, Exe, true);
                CriarAtalho(AtalhoMenu);
                CriarAtalho(AtalhoDesktop);
                App.LiberarInstancia();
                Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true, WorkingDirectory = Pasta });
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Não consegui instalar: " + ex.Message + "\n\nVou abrir esta cópia mesmo.", "Saúde do Notebook");
                return false;
            }
        }

        static void CriarAtalho(string caminho)
        {
            try
            {
                var tipo = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(tipo);
                dynamic lnk = shell.CreateShortcut(caminho);
                lnk.TargetPath = Exe;
                lnk.WorkingDirectory = Pasta;
                lnk.IconLocation = Exe + ",0";
                lnk.Description = "Saúde, desempenho e segurança do notebook";
                lnk.Save();
            }
            catch { }
        }
    }
}
