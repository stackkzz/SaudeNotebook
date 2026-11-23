using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace SaudeNotebook
{
    public partial class App : Application
    {
        static Mutex _mutex;
        static bool _donoMutex;
        static EventWaitHandle _sinalMostrar;

        public static bool IniciouMinimizado { get; private set; }
        public static bool AcabouDeAtualizar { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();

            // Modo servidor MCP: o app do Claude conversa com o .exe por stdin/stdout, sem janela
            if (args.Contains("--mcp"))
            {
                try { McpServidor.Rodar(); }
                catch (Exception ex) { try { Console.Error.WriteLine(ex); } catch { } }
                Shutdown();
                return;
            }

            DispatcherUnhandledException += ErroNaoTratado;
            IniciouMinimizado = args.Contains("--minimizado");
            AcabouDeAtualizar = args.Contains("--atualizado");

            // Uma instância só: se já estiver aberto, pede para a outra aparecer e sai
            _sinalMostrar = new EventWaitHandle(false, EventResetMode.AutoReset, "SaudeNotebook_Mostrar");
            _mutex = new Mutex(false, "SaudeNotebook_Instancia");
            try { _donoMutex = _mutex.WaitOne(AcabouDeAtualizar ? 15000 : 0); }
            catch (AbandonedMutexException) { _donoMutex = true; }
            if (!_donoMutex)
            {
                _sinalMostrar.Set();
                Shutdown();
                return;
            }

            Atualizador.LimparAntigo();
            if (Instalador.OferecerInstalacao())
            {
                Shutdown();
                return;
            }
            Inicializacao.MigrarVersaoAntiga();
            ConexaoClaude.AtualizarCaminho();

            var janela = new MainWindow();
            MainWindow = janela;
            var espera = new Thread(() =>
            {
                while (true)
                {
                    _sinalMostrar.WaitOne();
                    Dispatcher.BeginInvoke(new Action(() => janela.Mostrar()));
                }
            }) { IsBackground = true };
            espera.Start();

            if (!IniciouMinimizado) janela.Show();
            janela.Iniciar();
        }

        public static void LiberarInstancia()
        {
            try
            {
                if (_donoMutex) { _mutex.ReleaseMutex(); _donoMutex = false; }
            }
            catch { }
        }

        void ErroNaoTratado(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Pastas.Dados);
                File.AppendAllText(Path.Combine(Pastas.Dados, "erros.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Exception}\n\n");
            }
            catch { }
            e.Handled = true;
        }
    }
}
