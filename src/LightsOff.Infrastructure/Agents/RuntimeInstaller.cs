using System.Text;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Agents;

public static class RuntimeInstaller
{
    public static readonly string[] SupportedRuntimes = { "node", "dotnet", "python", "go", "java", "ruby", "rust" };

    public static string GetInstallScript(RuntimeRequirement req) => req.Runtime.ToLowerInvariant() switch
    {
        "node" => $"""
            echo '>>> Installing Node.js {req.Version}'
            apt-get install -y curl ca-certificates gnupg
            curl -fsSL https://deb.nodesource.com/setup_{req.Version}.x | bash -
            apt-get install -y nodejs
            echo "node $(node --version) installed"
            """,
        "dotnet" => $"""
            echo '>>> Installing .NET SDK {req.Version}'
            apt-get install -y wget
            wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
            chmod +x /tmp/dotnet-install.sh
            /tmp/dotnet-install.sh --channel {req.Version} --install-dir /usr/share/dotnet
            ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
            echo "dotnet $(dotnet --version) installed"
            """,
        "python" => $"""
            echo '>>> Installing Python'
            apt-get install -y python3 python3-pip python3-venv
            ln -sf /usr/bin/python3 /usr/bin/python
            echo "python $(python3 --version) installed"
            """,
        "go" => $"""
            echo '>>> Installing Go {req.Version}'
            apt-get install -y wget
            wget -q https://go.dev/dl/go{req.Version}.linux-amd64.tar.gz -O /tmp/go.tar.gz
            tar -C /usr/local -xzf /tmp/go.tar.gz
            ln -sf /usr/local/go/bin/go /usr/bin/go
            echo "go $(go version) installed"
            """,
        "java" => $"""
            echo '>>> Installing Java {req.Version}'
            apt-get install -y openjdk-{req.Version}-jdk
            echo "java $(java -version 2>&1 | head -1) installed"
            """,
        "ruby" => """
            echo '>>> Installing Ruby'
            apt-get install -y ruby-full
            echo "ruby $(ruby --version) installed"
            """,
        "rust" => """
            echo '>>> Installing Rust'
            apt-get install -y curl
            curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y
            export PATH="/root/.cargo/bin:$PATH"
            echo "rust $(rustc --version) installed"
            """,
        _ => $"echo 'Unknown runtime: {req.Runtime} — skipping'"
    };

    public static string GenerateInstallScript(RepoDependencyProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/sh");
        sb.AppendLine("set -e");
        sb.AppendLine("export DEBIAN_FRONTEND=noninteractive");
        sb.AppendLine("apt-get update -qq 2>/dev/null || true");
        sb.AppendLine();

        // Install runtimes (deduplicate by runtime name, first version wins)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rt in profile.Runtimes)
        {
            if (!seen.Add(rt.Runtime)) continue;
            sb.AppendLine(GetInstallScript(rt));
            sb.AppendLine();
        }

        // Package install commands
        if (profile.PackageInstallCommands.Count > 0)
        {
            sb.AppendLine("echo '>>> Installing packages'");
            foreach (var cmd in profile.PackageInstallCommands)
            {
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                sb.AppendLine(cmd);
            }
            sb.AppendLine();
        }

        // Custom user commands
        if (profile.CustomCommands.Count > 0)
        {
            sb.AppendLine("echo '>>> Running custom commands'");
            foreach (var cmd in profile.CustomCommands)
            {
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                sb.AppendLine(cmd);
            }
        }

        sb.AppendLine("echo '>>> Dependencies installed'");
        return sb.ToString();
    }
}
