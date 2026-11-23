using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using Desenho = System.Drawing;

namespace SaudeNotebook
{
    public partial class MainWindow : Window
    {
        readonly ColetorAoVivo _coletor = new ColetorAoVivo();
        readonly Analise _analise = new Analise();
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        readonly DispatcherTimer _timerAtualizacao = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        readonly List<(double Cpu, double Ram, double Gpu)> _historico = new List<(double, double, double)>();
        readonly Dictionary<string, DateTime> _notificados = new Dictionary<string, DateTime>();
        readonly BrushConverter _conv = new BrushConverter();

        WF.NotifyIcon _icone;
        Polyline _linhaCpu, _linhaRam, _linhaGpu;
        Line[] _guias;

        int _ordem;
        bool _coletando, _scanRodando, _saindo, _avisouBandeja, _scanAutoFeito;
        int _tick;
        Dados _ultimo;
        List<Alerta> _alertas = new List<Alerta>();
        ResultadoEngasgos _eng;
        List<Achado> _scan = new List<Achado>();
        DateTime _inicioScan;
        Lancamento _lancamento;

        public MainWindow()
        {
            InitializeComponent();

            var cfg = Config.Atual;
            NotificarChk.IsChecked = cfg.Notificar;
            BandejaChk.IsChecked = cfg.Bandeja;
            GravarHistChk.IsChecked = cfg.GravarHist;
            IniciarWindowsChk.IsChecked = Inicializacao.Ativo;

            if (Nativo.EhAdmin()) AvisoAdmin.Visibility = Visibility.Collapsed;
            PastaTexto.Text = "Histórico e configurações: " + Pastas.Dados + "\nRelatórios HTML: " + Pastas.Relatorios;
            VersaoTexto.Text = Txt.F("Você está na versão {0}. O app confere sozinho se tem versão nova ao abrir e a cada 6 horas.", Atualizador.AtualTexto);
            Title = "Saúde do Notebook " + Atualizador.AtualTexto;

            // Gráfico
            _guias = new Line[3];
            for (int i = 0; i < 3; i++)
            {
                _guias[i] = new Line { Stroke = Pincel("#262A33"), StrokeThickness = 1 };
                Grafico.Children.Add(_guias[i]);
            }
            _linhaRam = NovaLinha("#F5B942");
            _linhaGpu = NovaLinha("#A78BFA");
            _linhaCpu = NovaLinha("#60A5FA");

            CriarIcone();

            _timer.Tick += async (s, e) => await Ciclo();
            _timerAtualizacao.Tick += async (s, e) => await VerificarAtualizacao(false);
            StateChanged += Janela_StateChanged;
            Closing += Janela_Closing;
        }

        /// <summary>Chamado pelo App depois de criar a janela.</summary>
        public async void Iniciar()
        {
            _timer.Start();
            _timerAtualizacao.Start();
            if (App.IniciouMinimizado)
                _icone.ShowBalloonTip(5000, "Saúde do Notebook", "Monitorando em segundo plano. Dê dois cliques no ícone perto do relógio para abrir.", WF.ToolTipIcon.Info);
            if (App.AcabouDeAtualizar)
                _icone.ShowBalloonTip(5000, "Saúde do Notebook atualizado", "Agora você está na versão " + Atualizador.AtualTexto + ".", WF.ToolTipIcon.Info);
            await Ciclo();
            await Task.Delay(4000);
            await VerificarAtualizacao(false);
        }

        public void Mostrar()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        // ================================================================
        //  Coleta ao vivo
        // ================================================================
        async Task Ciclo()
        {
            if (_coletando || _saindo) return;
            _coletando = true;
            try
            {
                var d = await Task.Run(() => _coletor.Coletar());
                MostrarDados(d);
                if (_tick >= 2 && !_scanAutoFeito)
                {
                    _scanAutoFeito = true;
                    _ = IniciarScan();
                }
            }
            catch (Exception ex)
            {
                Rodape.Text = "Erro na coleta: " + ex.Message;
            }
            finally { _coletando = false; }
        }

        void MostrarDados(Dados d)
        {
            _tick++;
            _ultimo = d;
            var erros = new List<string>();
            void Seguro(string parte, Action a) { try { a(); } catch (Exception ex) { erros.Add(parte + ": " + ex.Message); } }

            if (_tick == 1)
            {
                string gpus = string.Join(" + ", d.Gpus);
                Contexto.Maquina = Txt.F("{0} • {1} • {2:N1} GB de RAM • {3} • Windows {4}", Environment.MachineName, d.CpuNome, d.RamTotalGB, gpus, Environment.OSVersion.Version);
                Maquina.Text = Contexto.Maquina;
                Maquina.ToolTip = Contexto.Maquina;
            }

            Seguro("cartões", () => AtualizarCartoes(d));
            Seguro("gráfico", () =>
            {
                _historico.Add((d.Cpu, d.RamPct, Math.Max(0, d.Gpu)));
                while (_historico.Count > 120) _historico.RemoveAt(0);
                DesenharGrafico();
            });
            Seguro("vazamentos", () =>
            {
                _analise.RastrearVazamentos(d, _tick);
                var vaz = new HashSet<int>(_analise.Vazamentos.Select(v => v.Pid));
                foreach (var p in d.TodosProcs)
                {
                    if (p.Pid == Environment.ProcessId) p.Alerta = "este app";
                    if (vaz.Contains(p.Pid)) p.Alerta = string.Join(", ", new[] { p.Alerta, "vazando memória?" }.Where(x => !string.IsNullOrEmpty(x)));
                }
            });
            Seguro("processos", () => { if (PausarProc.IsChecked != true) AtualizarProcessos(); });
            Seguro("engasgos", () =>
            {
                _eng = _analise.Engasgos(d);
                EngGrid.ItemsSource = _eng.Linhas;
                Veredito.Text = _eng.Veredito;
                Veredito.Foreground = Pincel(_eng.Pior >= 3 ? "#FF6B6B" : _eng.Pior >= 2 ? "#F5B942" : "#4ADE80");
                Culpados.Text = _eng.Culpados;
                MemDetalhe.Text = _eng.MemDetalhe;
                MemCulpados.Text = _eng.MemCulpados;
            });
            Seguro("alertas", () =>
            {
                _alertas = _analise.Alertas(d);
                AlertasGrid.ItemsSource = _alertas.Count > 0
                    ? (System.Collections.IEnumerable)_alertas
                    : new List<Alerta> { new Alerta { Severidade = "OK", Problema = "Nenhum alerta", Detalhe = "Tudo dentro do normal agora." } };
                Notificar();
            });
            Seguro("histórico", () => { if (GravarHistChk.IsChecked == true && _tick % 10 == 1) Relatorio.GravarHistorico(d, _alertas); });
            Seguro("status", AtualizarStatus);

            string rod = Txt.F("Versão {0} • atualizado às {1:HH:mm:ss} • coleta em {2} ms • {3}", Atualizador.AtualTexto, DateTime.Now, d.ColetaMs,
                Nativo.EhAdmin() ? "administrador" : "modo limitado");
            if (erros.Count > 0) rod += " • erro: " + string.Join(" | ", erros);
            Rodape.Text = rod;
        }

        void AtualizarCartoes(Dados d)
        {
            CpuValor.Text = Txt.F("{0:N0}%", d.Cpu);
            Barra(CpuBarra, d.Cpu, 70, 90);
            var s = new List<string>();
            if (d.DesempenhoPct > 0 && d.CpuMaxMHz > 0) s.Add(Txt.F("{0:N1} GHz", d.CpuMaxMHz * d.DesempenhoPct / 100000));
            if (d.TempC > 0) s.Add(Txt.F("{0} °C", d.TempC));
            s.Add(d.Nucleos + " threads");
            CpuSub.Text = string.Join(" • ", s);

            RamValor.Text = Txt.F("{0:N0}%", d.RamPct);
            Barra(RamBarra, d.RamPct, 80, 90);
            RamSub.Text = Txt.F("{0:N1} de {1:N1} GB livres", d.RamLivreGB, d.RamTotalGB);

            if (d.Gpu >= 0)
            {
                GpuValor.Text = Txt.F("{0:N0}%", d.Gpu);
                Barra(GpuBarra, d.Gpu, 85, 98);
                var tg = Analise.Top(d, p => p.Gpu);
                GpuSub.Text = tg != null && tg.Gpu > 1 ? "mais usado: " + tg.Nome : "quase parada";
            }
            else { GpuValor.Text = "n/d"; GpuSub.Text = "contador indisponível"; }

            DiscoValor.Text = Txt.F("{0:N0}%", d.DiscoPct);
            Barra(DiscoBarra, d.DiscoPct, 80, 95);
            var menor = d.Unidades.OrderBy(u => u.LivrePct).FirstOrDefault();
            DiscoSub.Text = menor != null ? Txt.F("{0:N1} MB/s • {1} {2:N0} GB livres", d.DiscoMBs, menor.Letra, menor.LivreGB) : Txt.F("{0:N1} MB/s", d.DiscoMBs);

            double tot = d.RedeEnvioKBs + d.RedeRecebKBs;
            RedeValor.Text = Txt.Taxa(tot);
            RedeSub.Text = "envio " + Txt.Taxa(d.RedeEnvioKBs) + " • recebe " + Txt.Taxa(d.RedeRecebKBs);
            RedeBarra.Value = Math.Min(100, tot / 50);

            if (d.TemBateria && d.BatPct >= 0)
            {
                BatValor.Text = d.BatPct + "%";
                string estado;
                if (d.NaTomada) estado = d.CargaW > 0 ? Txt.F("Carregando {0:N1} W", d.CargaW) : "Na tomada";
                else
                {
                    estado = Txt.F("Gastando {0:N1} W", d.DescargaW);
                    if (d.DescargaW > 0) estado += " • ~" + Txt.Minutos(d.BatRestanteWh / d.DescargaW * 60);
                }
                if (d.BatSaude > 0) estado += " • saúde " + d.BatSaude + "%";
                BatSub.Text = estado;
                BatSub.ToolTip = estado;
                BatBarra.Value = d.BatPct;
                BatBarra.Foreground = Pincel(d.BatPct <= 15 ? "#FF6B6B" : d.BatPct <= 30 ? "#F5B942" : "#4ADE80");
            }
            else { BatValor.Text = "—"; BatSub.Text = "sem bateria detectada"; }
        }

        void AtualizarProcessos()
        {
            var d = _ultimo;
            if (d == null) return;
            Func<ProcInfo, double> campo;
            switch (_ordem)
            {
                case 1: campo = p => p.Gpu; break;
                case 2: campo = p => p.RamMB; break;
                case 3: campo = p => p.DiscoKBs; break;
                case 4: campo = p => p.Faltas; break;
                default: campo = p => p.Cpu; break;
            }

            List<ProcInfo> lista;
            bool agrupar = Agrupar.IsChecked == true;
            if (agrupar)
            {
                var fonte = d.TodosProcs.Count > 0 ? d.TodosProcs : d.Procs;
                lista = fonte.GroupBy(p => p.Nome, StringComparer.OrdinalIgnoreCase).Select(g => new ProcInfo
                {
                    Pid = g.First().Pid,
                    Nome = g.Key,
                    Qtd = g.Count(),
                    Cpu = Math.Round(g.Sum(p => p.Cpu), 1),
                    Gpu = Math.Min(100, Math.Round(g.Sum(p => p.Gpu), 1)),
                    RamMB = g.Sum(p => p.RamMB),
                    DiscoKBs = g.Sum(p => p.DiscoKBs),
                    Faltas = g.Sum(p => p.Faltas),
                    Alerta = string.Join(", ", g.Select(p => p.Alerta).Where(x => !string.IsNullOrEmpty(x)).Distinct()),
                    Caminho = g.Select(p => p.Caminho).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? ""
                }).OrderByDescending(campo).Take(80).ToList();
            }
            else lista = (d.TodosProcs.Count > 0 ? d.TodosProcs : d.Procs).OrderByDescending(campo).Take(60).ToList();

            var sel = ProcGrid.SelectedItem as ProcInfo;
            ProcGrid.ItemsSource = lista;
            if (sel != null)
            {
                var igual = agrupar
                    ? lista.FirstOrDefault(p => string.Equals(p.Nome, sel.Nome, StringComparison.OrdinalIgnoreCase))
                    : lista.FirstOrDefault(p => p.Pid == sel.Pid);
                if (igual != null) ProcGrid.SelectedItem = igual;
            }
        }

        void Notificar()
        {
            foreach (var a in _alertas)
            {
                if (Txt.Nivel(a.Severidade) < 2) continue;
                if (_notificados.TryGetValue(a.Chave, out var ult) && (DateTime.Now - ult).TotalMinutes < 15) continue;
                _notificados[a.Chave] = DateTime.Now;
                if (NotificarChk.IsChecked != true) continue;
                string txt = a.Detalhe ?? "";
                if (txt.Length > 240) txt = txt.Substring(0, 237) + "...";
                _icone.ShowBalloonTip(8000, a.Problema, txt, a.Severidade == "Crítico" ? WF.ToolTipIcon.Error : WF.ToolTipIcon.Warning);
            }
        }

        void AtualizarStatus()
        {
            int pior = 0, qtd = 0;
            foreach (var sev in _alertas.Select(a => a.Severidade).Concat(_scan.Select(x => x.Severidade)))
            {
                int n = Txt.Nivel(sev);
                if (n >= 2) qtd++;
                if (n > pior) pior = n;
            }
            string bg, fg, txt;
            if (pior >= 3) { bg = "#3A1D20"; fg = "#FF6B6B"; txt = Txt.F("Crítico • {0} ponto(s) para olhar", qtd); }
            else if (pior >= 2) { bg = "#3A3119"; fg = "#F5B942"; txt = Txt.F("Atenção • {0} ponto(s) para olhar", qtd); }
            else { bg = "#1C2A22"; fg = "#4ADE80"; txt = "Tudo certo"; }
            StatusCaixa.Background = Pincel(bg);
            StatusTexto.Foreground = Pincel(fg);
            StatusTexto.Text = txt;
            string dica = "Saúde do Notebook: " + txt;
            _icone.Text = dica.Length > 63 ? dica.Substring(0, 63) : dica;
        }

        // ================================================================
        //  Gráfico
        // ================================================================
        Polyline NovaLinha(string cor)
        {
            var l = new Polyline { Stroke = Pincel(cor), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round };
            Grafico.Children.Add(l);
            return l;
        }

        void DesenharGrafico()
        {
            double w = Grafico.ActualWidth, h = Grafico.ActualHeight;
            if (w <= 0 || h <= 0) return;
            for (int i = 0; i < 3; i++)
            {
                double y = Math.Round(h * (i + 1) / 4) + 0.5;
                _guias[i].X1 = 0; _guias[i].X2 = w; _guias[i].Y1 = y; _guias[i].Y2 = y;
            }
            int n = _historico.Count;
            if (n < 2) return;
            double passo = w / 119;
            var pc = new PointCollection(); var pr = new PointCollection(); var pg = new PointCollection();
            for (int i = 0; i < n; i++)
            {
                double x = w - (n - 1 - i) * passo;
                pc.Add(new Point(x, Y(_historico[i].Cpu, h)));
                pr.Add(new Point(x, Y(_historico[i].Ram, h)));
                pg.Add(new Point(x, Y(_historico[i].Gpu, h)));
            }
            _linhaCpu.Points = pc; _linhaRam.Points = pr; _linhaGpu.Points = pg;
        }

        static double Y(double v, double h) => h - (Math.Max(0, Math.Min(100, v)) / 100) * (h - 4) - 2;

        void Grafico_SizeChanged(object sender, SizeChangedEventArgs e) => DesenharGrafico();

        // ================================================================
        //  Varredura
        // ================================================================
        async Task IniciarScan()
        {
            if (_scanRodando) return;
            _scanRodando = true;
            BtnScan.IsEnabled = false;
            ScanStatus.Text = "Analisando... leva de 30 s a 2 min. O monitor ao vivo continua funcionando.";
            _inicioScan = DateTime.Now;
            try
            {
                _scan = await Task.Run(() => Varredura.Executar());
                MostrarScan();
                int crit = _scan.Count(x => x.Severidade == "Crítico"), aten = _scan.Count(x => x.Severidade == "Atenção");
                ScanStatus.Text = Txt.F("Concluída às {0:HH:mm} em {1:N0} s • {2} crítico(s) • {3} de atenção • {4} itens verificados.",
                    DateTime.Now, (DateTime.Now - _inicioScan).TotalSeconds, crit, aten, _scan.Count);
                AtualizarStatus();
                if (crit > 0 && NotificarChk.IsChecked == true)
                    _icone.ShowBalloonTip(8000, "Varredura encontrou problemas", crit + " item(ns) crítico(s). Abra a aba Segurança e saúde.", WF.ToolTipIcon.Error);
            }
            catch (Exception ex) { ScanStatus.Text = "Erro na varredura: " + ex.Message; }
            finally
            {
                _scanRodando = false;
                BtnScan.IsEnabled = true;
            }
        }

        void MostrarScan()
        {
            ScanGrid.ItemsSource = SoProblemas.IsChecked == true ? _scan.Where(x => Txt.Nivel(x.Severidade) >= 2).ToList() : _scan;
        }

        // ================================================================
        //  Atualizações
        // ================================================================
        async Task VerificarAtualizacao(bool manual)
        {
            try
            {
                var l = await Atualizador.VerificarAsync();
                if (Atualizador.EhMaisNova(l))
                {
                    _lancamento = l;
                    if (!manual && Config.Atual.VersaoIgnorada == l.Tag) return;
                    string notas = (l.Notas ?? "").Trim();
                    if (notas.Length > 300) notas = notas.Substring(0, 297) + "...";
                    TextoAtualizacao.Text = Txt.F("Versão {0} disponível (você está na {1}).{2}", l.Tag, Atualizador.AtualTexto,
                        notas != "" ? "  Novidades: " + notas.Replace("\r", "").Replace("\n", "  ") : "");
                    AvisoAtualizacao.Visibility = Visibility.Visible;
                    if (!IsVisible)
                        _icone.ShowBalloonTip(6000, "Atualização disponível", "Versão " + l.Tag + " do Saúde do Notebook. Abra o app para atualizar.", WF.ToolTipIcon.Info);
                }
                else if (manual)
                {
                    MessageBox.Show(this, "Você já está na versão mais nova (" + Atualizador.AtualTexto + ").", "Saúde do Notebook");
                }
            }
            catch (Exception ex)
            {
                if (manual) MessageBox.Show(this, "Não consegui verificar agora: " + ex.Message, "Saúde do Notebook");
            }
        }

        async void BtnAtualizar_Click(object sender, RoutedEventArgs e)
        {
            if (_lancamento == null) return;
            BtnAtualizar.IsEnabled = false;
            try
            {
                var prog = new Progress<double>(p => BtnAtualizar.Content = Txt.F("Baixando {0:N0}%", p));
                bool ok = await Atualizador.InstalarAsync(_lancamento, prog);
                if (ok) { Sair(); return; }
                MessageBox.Show(this, "Não consegui instalar a atualização sozinho. Vou abrir a página para você baixar.", "Saúde do Notebook");
                Abrir.Url(_lancamento.UrlPagina ?? Atualizador.PaginaReleases);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Falhou ao atualizar: " + ex.Message + "\n\nVou abrir a página para você baixar.", "Saúde do Notebook");
                Abrir.Url(_lancamento.UrlPagina ?? Atualizador.PaginaReleases);
            }
            finally
            {
                BtnAtualizar.IsEnabled = true;
                BtnAtualizar.Content = "Atualizar agora";
            }
        }

        void BtnNovidades_Click(object sender, RoutedEventArgs e) => Abrir.Url(_lancamento?.UrlPagina ?? Atualizador.PaginaReleases);

        void BtnDepois_Click(object sender, RoutedEventArgs e)
        {
            AvisoAtualizacao.Visibility = Visibility.Collapsed;
            if (_lancamento != null) { Config.Atual.VersaoIgnorada = _lancamento.Tag; Config.Atual.Salvar(); }
        }

        async void BtnVerificar_Click(object sender, RoutedEventArgs e) => await VerificarAtualizacao(true);

        // ================================================================
        //  Claude
        // ================================================================
        void CopiarLinha_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.DataContext is ICopiavel item)) return;
            Copiar(item.TextoCopia(), b);
        }

        void BtnClaude_Click(object sender, RoutedEventArgs e)
        {
            string texto = Relatorio.TextoClaude(_ultimo, _alertas, _eng, _scan);
            if (Area.Copiar(texto))
            {
                Abrir.Url("https://claude.ai/new");
                Rodape.Text = "Diagnóstico copiado. No Claude, cole com Ctrl+V e envie.";
                _icone.ShowBalloonTip(4000, "Diagnóstico copiado", "No Claude, cole com Ctrl+V e envie.", WF.ToolTipIcon.Info);
            }
        }

        void BtnMemClaude_Click(object sender, RoutedEventArgs e)
        {
            if (_eng == null) return;
            string texto = Txt.F("Notebook: {0}\nO app Saúde do Notebook mostra isto sobre a memória agora:\n{1}\n{2}\n\n" +
                                 "Me explica o que está pesando na memória e como resolver, passo a passo?", Contexto.Maquina, _eng.MemDetalhe, _eng.MemCulpados);
            Copiar(texto, sender as Button);
        }

        void BtnProcClaude_Click(object sender, RoutedEventArgs e)
        {
            if (ProcGrid.SelectedItem is ProcInfo p) Copiar(p.TextoCopia(), sender as Button);
            else Aviso("Selecione um processo na lista.");
        }

        void Copiar(string texto, Button botao)
        {
            if (!Area.Copiar(texto)) { Aviso("Não consegui copiar. Tente de novo."); return; }
            Rodape.Text = "Copiado. Cole numa conversa com o Claude (Ctrl+V).";
            if (botao == null) return;
            var original = botao.Content;
            botao.Content = "Copiado ✓";
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (s, ev) => { botao.Content = original; t.Stop(); };
            t.Start();
        }

        // ================================================================
        //  Botões diversos
        // ================================================================
        void Ordem_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton r && int.TryParse(r.Tag?.ToString(), out var o)) _ordem = o;
            if (IsLoaded) AtualizarProcessos();
        }

        void ProcGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true; // a lista é recriada a cada 3 s; a ordem fica nos botões
            switch (e.Column.SortMemberPath)
            {
                case "Cpu": OrdCpu.IsChecked = true; break;
                case "Gpu": OrdGpu.IsChecked = true; break;
                case "RamMB": OrdRam.IsChecked = true; break;
                case "DiscoKBs": OrdDisco.IsChecked = true; break;
                case "Faltas": OrdFaltas.IsChecked = true; break;
            }
        }
        void AtualizarProcessos_Click(object sender, RoutedEventArgs e) { if (PausarProc.IsChecked != true || sender == Agrupar) AtualizarProcessos(); }
        async void BtnScan_Click(object sender, RoutedEventArgs e) => await IniciarScan();
        void SoProblemas_Click(object sender, RoutedEventArgs e) => MostrarScan();
        void BtnResmon_Click(object sender, RoutedEventArgs e) => Abrir.Programa("resmon.exe");
        void BtnPasta_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(Pastas.Dados); Abrir.Programa("explorer.exe", "\"" + Pastas.Dados + "\""); }

        void BtnLocal_Click(object sender, RoutedEventArgs e)
        {
            if (ProcGrid.SelectedItem is ProcInfo p && !string.IsNullOrEmpty(p.Caminho)) Abrir.Programa("explorer.exe", "/select,\"" + p.Caminho + "\"");
            else Aviso("Selecione um processo que tenha caminho conhecido.");
        }

        void BtnPesquisar_Click(object sender, RoutedEventArgs e)
        {
            if (ProcGrid.SelectedItem is ProcInfo p) Abrir.Url("https://www.google.com/search?q=" + Uri.EscapeDataString(p.Nome + ".exe o que é"));
            else Aviso("Selecione um processo.");
        }

        void BtnEncerrar_Click(object sender, RoutedEventArgs e)
        {
            if (!(ProcGrid.SelectedItem is ProcInfo p)) { Aviso("Selecione um processo."); return; }
            string msg = p.Qtd > 1
                ? Txt.F("Encerrar todos os {0} processos de {1}?\n\nO que não estiver salvo nesse programa será perdido.", p.Qtd, p.Nome)
                : Txt.F("Encerrar {0} (PID {1})?\n\nO que não estiver salvo nesse programa será perdido. Encerrar processos do Windows pode travar o PC.", p.Nome, p.Pid);
            if (MessageBox.Show(this, msg, "Encerrar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                if (p.Qtd > 1) foreach (var pr in Process.GetProcessesByName(p.Nome)) { try { pr.Kill(); } catch { } }
                else Process.GetProcessById(p.Pid).Kill();
            }
            catch (Exception ex) { Aviso("Não consegui encerrar: " + ex.Message); }
        }

        void BtnExportar_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string arq = Relatorio.Html(_ultimo, _alertas, _eng, _scan, StatusTexto.Text);
                Abrir.Url(arq);
            }
            catch (Exception ex) { Aviso("Não consegui exportar: " + ex.Message); }
        }

        void BtnBateria_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Pastas.Dados);
                string arq = System.IO.Path.Combine(Pastas.Dados, "battery-report.html");
                var psi = new ProcessStartInfo("powercfg.exe", "/batteryreport /output \"" + arq + "\"") { CreateNoWindow = true, UseShellExecute = false };
                using (var pr = Process.Start(psi)) pr.WaitForExit(15000);
                Abrir.Url(arq);
            }
            catch (Exception ex) { Aviso("Não consegui gerar o relatório: " + ex.Message); }
        }

        void BtnAdmin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                App.LiberarInstancia();
                Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas" });
                Sair();
            }
            catch
            {
                // Cancelou o pedido de administrador: continua aberto
            }
        }

        void Opcao_Click(object sender, RoutedEventArgs e)
        {
            var cfg = Config.Atual;
            cfg.Notificar = NotificarChk.IsChecked == true;
            cfg.Bandeja = BandejaChk.IsChecked == true;
            cfg.GravarHist = GravarHistChk.IsChecked == true;
            cfg.Salvar();
        }

        void IniciarWindows_Click(object sender, RoutedEventArgs e)
        {
            try { Inicializacao.Definir(IniciarWindowsChk.IsChecked == true); }
            catch (Exception ex)
            {
                Aviso("Não consegui mudar a inicialização: " + ex.Message);
                IniciarWindowsChk.IsChecked = Inicializacao.Ativo;
            }
        }

        void Aviso(string texto) => MessageBox.Show(this, texto, "Saúde do Notebook");

        Brush Pincel(string hex) => (Brush)_conv.ConvertFromString(hex);

        void Barra(ProgressBar b, double v, double atencao, double critico)
        {
            if (v < 0) v = 0;
            b.Value = Math.Min(100, v);
            b.Foreground = Pincel(v >= critico ? "#FF6B6B" : v >= atencao ? "#F5B942" : "#4ADE80");
        }

        // ================================================================
        //  Bandeja e saída
        // ================================================================
        void CriarIcone()
        {
            _icone = new WF.NotifyIcon { Text = "Saúde do Notebook", Visible = true };
            try { _icone.Icon = Desenho.Icon.ExtractAssociatedIcon(Environment.ProcessPath); }
            catch { _icone.Icon = Desenho.SystemIcons.Shield; }
            _icone.DoubleClick += (s, e) => Mostrar();
            _icone.BalloonTipClicked += (s, e) => Mostrar();

            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Abrir", null, (s, e) => Mostrar());
            menu.Items.Add("Pedir ajuda ao Claude", null, (s, e) => BtnClaude_Click(null, null));
            menu.Items.Add("Fazer varredura completa", null, async (s, e) => { Mostrar(); Abas.SelectedIndex = 3; await IniciarScan(); });
            menu.Items.Add("Verificar atualizações", null, async (s, e) => { Mostrar(); await VerificarAtualizacao(true); });
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Sair", null, (s, e) => Sair());
            _icone.ContextMenuStrip = menu;
        }

        void Janela_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && BandejaChk.IsChecked == true)
            {
                Hide();
                if (!_avisouBandeja)
                {
                    _avisouBandeja = true;
                    _icone.ShowBalloonTip(5000, "Saúde do Notebook", "Continuo monitorando aqui. Dê dois cliques no ícone para abrir.", WF.ToolTipIcon.Info);
                }
            }
        }

        void Janela_Closing(object sender, CancelEventArgs e)
        {
            if (!_saindo) Sair(fecharJanela: false);
        }

        void Sair(bool fecharJanela = true)
        {
            if (_saindo) return;
            _saindo = true;
            _timer.Stop();
            _timerAtualizacao.Stop();
            try { _icone.Visible = false; _icone.Dispose(); } catch { }
            if (fecharJanela) { try { Close(); } catch { } }
            Application.Current.Shutdown();
        }
    }
}
