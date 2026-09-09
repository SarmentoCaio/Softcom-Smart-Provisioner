using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SoftcomSmartProvisioner;

public sealed class SoftcomshopLoginForm : Form
{
    // Credenciais de fabrica solicitadas para o fluxo automatico.
    // Observacao: qualquer segredo embutido em um executavel desktop pode ser recuperado
    // por quem tiver acesso ao binario. Nao registrar estas senhas em logs.
    private const string AutomaticEmail = "fabrica@softcomtecnologia.com.br";
    private static readonly string[] AutomaticPasswords = ["seller1", "fab1478"];

    private readonly WebView2 _webView = new();
    private readonly CoreWebView2Environment _environment;
    private readonly string _targetUrl;
    private readonly string _host;
    private readonly Label _info = new();
    private bool _initialNavigationCompleted;
    private bool _automaticSubmitInProgress;
    private bool _manualMode;
    private int _automaticAttempt;

    public SoftcomshopLoginForm(CoreWebView2Environment environment, string targetUrl)
    {
        _environment = environment;
        _targetUrl = targetUrl;
        _host = new Uri(targetUrl).Host;

        Text = "Entrar no Softcomshop";
        StartPosition = FormStartPosition.CenterParent;
        Width = 1080;
        Height = 760;
        MinimumSize = new Size(860, 620);
        BackColor = Color.FromArgb(11, 18, 32);

        // O formulario inicia invisivel. Ele so aparece se as duas credenciais automaticas falharem.
        Opacity = 0;
        ShowInTaskbar = false;

        var top = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = Color.FromArgb(14, 23, 40)
        };
        _info.Dock = DockStyle.Fill;
        _info.Text = "Tentando autenticar automaticamente no Softcomshop...";
        _info.ForeColor = Color.Gainsboro;
        _info.TextAlign = ContentAlignment.MiddleLeft;

        var done = new Button
        {
            Dock = DockStyle.Right,
            Width = 150,
            Text = "Continuar",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(255, 106, 0)
        };
        done.FlatAppearance.BorderSize = 0;
        done.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        top.Controls.Add(_info);
        top.Controls.Add(done);
        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);
        Controls.Add(top);

        Load += OnLoad;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        await _webView.EnsureCoreWebView2Async(_environment);
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.Source = new Uri(_targetUrl);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _webView.Source is null)
        {
            if (!_manualMode && _automaticAttempt >= AutomaticPasswords.Length)
            {
                RevealForManualLogin("Nao foi possivel concluir o login automatico. Informe usuario e senha.");
            }
            return;
        }

        var uri = _webView.Source;
        if (!string.Equals(uri.Host, _host, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isLogin = IsLoginPath(uri.AbsolutePath);

        if (!_initialNavigationCompleted)
        {
            _initialNavigationCompleted = true;
            if (!isLogin)
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
        }

        if (!isLogin)
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        if (_manualMode)
        {
            return;
        }

        // Voltou para a tela de login apos um submit: a tentativa anterior falhou.
        if (_automaticSubmitInProgress)
        {
            _automaticSubmitInProgress = false;
        }

        if (_automaticAttempt >= AutomaticPasswords.Length)
        {
            RevealForManualLogin("As credenciais automaticas nao foram aceitas. Informe usuario e senha para continuar.");
            return;
        }

        var password = AutomaticPasswords[_automaticAttempt++];
        var submitted = await TrySubmitCredentialsAsync(AutomaticEmail, password);
        if (submitted)
        {
            _automaticSubmitInProgress = true;
            return;
        }

        // Se a pagina de login mudou e nao foi possivel identificar o formulario, nao insiste cegamente.
        RevealForManualLogin("Nao foi possivel identificar automaticamente o formulario de login. Informe usuario e senha.");
    }

    private async Task<bool> TrySubmitCredentialsAsync(string email, string password)
    {
        var emailJson = JsonSerializer.Serialize(email);
        var passwordJson = JsonSerializer.Serialize(password);
        var script = $$"""
            (() => {
              const email = {{emailJson}};
              const password = {{passwordJson}};
              const inputs = Array.from(document.querySelectorAll('input'));
              const emailInput = document.querySelector('input[name="email"], input[type="email"], input[id*="email" i], input[name*="usuario" i], input[id*="usuario" i]')
                || inputs.find(x => /email|usuario|login/i.test(`${x.name || ''} ${x.id || ''} ${x.placeholder || ''}`));
              const passwordInput = document.querySelector('input[name="password"], input[type="password"], input[id*="senha" i], input[name*="senha" i]')
                || inputs.find(x => /password|senha/i.test(`${x.name || ''} ${x.id || ''} ${x.placeholder || ''}`));
              if (!emailInput || !passwordInput) return false;

              const setValue = (el, value) => {
                const proto = Object.getPrototypeOf(el);
                const descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
                if (descriptor && descriptor.set) descriptor.set.call(el, value);
                else el.value = value;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
              };

              setValue(emailInput, email);
              setValue(passwordInput, password);

              const form = emailInput.form || passwordInput.form || emailInput.closest('form') || passwordInput.closest('form');
              if (form) {
                if (typeof form.requestSubmit === 'function') form.requestSubmit();
                else form.submit();
                return true;
              }

              const button = document.querySelector('button[type="submit"], input[type="submit"]')
                || Array.from(document.querySelectorAll('button')).find(b => /entrar|login|acessar/i.test((b.innerText || b.textContent || '').trim()));
              if (button) {
                button.click();
                return true;
              }
              return false;
            })();
            """;

        try
        {
            var raw = await _webView.CoreWebView2.ExecuteScriptAsync(script);
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void RevealForManualLogin(string message)
    {
        if (_manualMode) return;
        _manualMode = true;
        _info.Text = message;
        ShowInTaskbar = true;
        Opacity = 1;
        Activate();
        BringToFront();
    }

    private static bool IsLoginPath(string path)
    {
        return path.Contains("login", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains("perfil-usuario", StringComparison.OrdinalIgnoreCase);
    }
}
