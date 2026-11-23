using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SaudeNotebook
{
    /// <summary>Registra o app como servidor MCP no app do Claude para desktop (claude_desktop_config.json).</summary>
    public static class ConexaoClaude
    {
        public const string Chave = "saude-notebook";

        /// <summary>Lugares onde o app do Claude guarda a configuração (instalação comum e versão da Microsoft Store).</summary>
        static List<string> Arquivos(bool incluirInexistentes)
        {
            var lista = new List<string>();
            string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
            if (incluirInexistentes || File.Exists(roaming)) lista.Add(roaming);
            try
            {
                string pacotes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                if (Directory.Exists(pacotes))
                {
                    foreach (var p in Directory.GetDirectories(pacotes, "Claude_*"))
                    {
                        string arq = Path.Combine(p, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
                        if (incluirInexistentes || File.Exists(arq)) lista.Add(arq);
                    }
                }
            }
            catch { }
            return lista.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool Conectado
        {
            get
            {
                foreach (var arq in Arquivos(false))
                {
                    try
                    {
                        var raiz = JsonNode.Parse(File.ReadAllText(arq)) as JsonObject;
                        if (raiz?["mcpServers"] is JsonObject s && s.ContainsKey(Chave)) return true;
                    }
                    catch { }
                }
                return false;
            }
        }

        /// <summary>Adiciona (ou atualiza) a entrada do app. Retorna quantos arquivos foram alterados.</summary>
        public static int Conectar()
        {
            int n = 0;
            string exe = Environment.ProcessPath;
            foreach (var arq in Arquivos(true))
            {
                // Só cria arquivo novo na pasta normal ou se a pasta do pacote da Store existir
                string pasta = Path.GetDirectoryName(arq);
                bool pastaDoPacote = arq.Contains(@"\Packages\");
                if (pastaDoPacote && !Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(pasta)))) continue;

                JsonObject raiz = new JsonObject();
                if (File.Exists(arq))
                {
                    string texto = File.ReadAllText(arq);
                    if (!string.IsNullOrWhiteSpace(texto))
                    {
                        raiz = JsonNode.Parse(texto) as JsonObject
                            ?? throw new Exception("O arquivo de configuração do Claude não está num formato que eu consiga editar: " + arq);
                    }
                    string copia = arq + ".antes-saudenotebook";
                    if (!File.Exists(copia)) File.Copy(arq, copia);
                }
                if (!(raiz["mcpServers"] is JsonObject servidores))
                {
                    servidores = new JsonObject();
                    raiz["mcpServers"] = servidores;
                }
                servidores[Chave] = new JsonObject
                {
                    ["command"] = exe,
                    ["args"] = new JsonArray("--mcp")
                };
                Directory.CreateDirectory(pasta);
                File.WriteAllText(arq, raiz.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                n++;
            }
            return n;
        }

        public static void Desconectar()
        {
            foreach (var arq in Arquivos(false))
            {
                try
                {
                    var raiz = JsonNode.Parse(File.ReadAllText(arq)) as JsonObject;
                    if (raiz?["mcpServers"] is JsonObject s && s.Remove(Chave))
                        File.WriteAllText(arq, raiz.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
            }
        }

        /// <summary>Se já está conectado, mantém o caminho do .exe atualizado (ex.: depois de instalar).</summary>
        public static void AtualizarCaminho()
        {
            try { if (Conectado) Conectar(); } catch { }
        }
    }
}
