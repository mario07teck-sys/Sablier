// Sablier : minuterie d'extinction et allumage programmé pour Windows.
// WPF sur .NET Framework 4.8 (présent sur tout Windows 10/11), compilé par build.ps1 avec le csc.exe de Windows (C# 5).
// Chaque programme est une tâche du Planificateur Windows (dossier \Sablier) : l'extinction a lieu même Sablier fermé ; l'icône de la zone de notification ne sert qu'à le rouvrir.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Windows.Automation.Peers;
using IO = System.IO;
using Forms = System.Windows.Forms;

static class Program
{
    // Un programme actif est prévu plus tard (sinon --sync ne fait rien)
    static bool Programmee()
    {
        try
        {
            foreach (Prog p in Planif.Liste()) if (p.Actif && p.Prochain > DateTime.Now) return true;
        }
        catch (Exception) { }
        return false;
    }

    [STAThread]
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        if (mode == "--wake") { Power.ResterEveille(); return 0; }
        if (mode == "--selftest") return SelfTest.Run();
        if (mode == "--uninstall") { Setup.Desinstaller(args.Length > 1 && args[1] == "--quiet"); return 0; }
        if (mode == "--silent") return Setup.Silencieux(); // installation sans fenêtre (winget)
        if (mode == "--sync") // à l'ouverture de session (clé Run) : ne reste dans la zone de notification que si quelque chose est prévu
        {
            Power.Synchroniser();
            if (!Setup.Installe || !Programmee()) return 0;
            mode = "--tray";
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources = Ui.Load<ResourceDictionary>("Theme.xaml");
        app.DispatcherUnhandledException += (s, e) => { MessageBox.Show(e.Exception.Message, "Sablier"); e.Handled = true; };

        Window w;
        if (mode == "--run" && args.Length > 1) w = new Alerte(args[1], 60).W;
        else if (mode == "--off") w = new Alerte("auto", 10).W;
        else if (!Setup.Installe) w = new Setup().W; // lancé hors de son dossier : c'est l'installeur
        else
        {
            // Une seule instance : une deuxième ouverture réaffiche la première (rien à montrer en --tray / --sync)
            bool nouveau;
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Sablier-Afficher", out nouveau);
            if (!nouveau) { if (mode != "--tray") signal.Set(); return 0; }
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown; // Sablier ne se ferme que par Quitter()
            // Clic droit sur Sablier épinglé à la barre des tâches → « Éteindre le PC »
            var jl = new JumpList();
            jl.JumpItems.Add(new JumpTask { Title = Ui.T("Éteindre le PC", "Shut down PC"), Description = Ui.T("Hibernation si un allumage est prévu, sinon arrêt", "Hibernates if a wake-up is scheduled, otherwise shuts down"), Arguments = "--off", IconResourcePath = Planif.Exe });
            JumpList.SetJumpList(app, jl);
            jl.Apply();
            var fen = new Fenetre(mode == "--tray", signal);
            if (mode == "--tray") { app.Run(); return 0; } // démarre caché dans la zone de notification
            w = fen.W;
        }
        return app.Run(w);
    }
}

// ---------- Programme = une tâche planifiée ----------

public class Prog
{
    static readonly string[] JoursEn = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    public string Nom { get; set; }
    public string Kind = "off";          // timer | off | on
    public string Action = "shutdown";   // shutdown | restart | sleep | hibernate | wake
    public string Jours = "0000000";     // lundi → dimanche
    public bool Repeter;
    public DateTime Debut, Cible, Prochain;
    public string Heure { get; set; }
    public bool Actif { get; set; }
    public bool Hiberne { get; set; }   // arrêt qui passera en veille prolongée car un allumage est prévu

    // Affichage dans la liste
    public string Glyph { get { return Ui.Glyph(Action); } }
    public string Titre { get { return Ui.Nom(Action); } }
    public string Sous { get { return Repeter ? Ui.Jours(Jours) : Ui.T("Une fois · ", "Once · ") + Ui.Jour(Cible); } }
    public Brush Fond { get { return (Brush)Application.Current.Resources[Kind == "on" ? "Sun" : "Grad"]; } }

    // Prochaine date à cette heure, un des jours cochés (aucun coché = n'importe quel jour)
    public static DateTime Prochaine(TimeSpan heure, string jours)
    {
        for (int i = 0; i < 8; i++)
        {
            DateTime d = DateTime.Today.AddDays(i).Add(heure);
            int j = ((int)d.DayOfWeek + 6) % 7;
            if (d > DateTime.Now && (jours == "0000000" || jours[j] == '1')) return d;
        }
        return DateTime.Today.AddDays(1).Add(heure);
    }

    // Minutes ramenées dans une journée (0 à 1439) : 23:50 + 15 min = 00:05
    public static int MinutesDansJour(int minutes) { return ((minutes % 1440) + 1440) % 1440; }

    public string Meta()
    {
        return string.Format(CultureInfo.InvariantCulture, "k={0};a={1};h={2};d={3};r={4};t0={5:s};t1={6:s}",
            Kind, Action, Heure, Jours, Repeter ? 1 : 0, Debut, Cible);
    }

    public static Prog Parse(string desc)
    {
        var d = new Dictionary<string, string>();
        foreach (string kv in (desc ?? "").Split(';'))
        {
            int i = kv.IndexOf('=');
            if (i > 0) d[kv.Substring(0, i)] = kv.Substring(i + 1);
        }
        if (!d.ContainsKey("k")) return null;
        var p = new Prog { Kind = d["k"], Action = Val(d, "a"), Heure = Val(d, "h"), Jours = Val(d, "d"), Repeter = Val(d, "r") == "1" };
        if (p.Jours.Length != 7) p.Jours = "0000000";
        DateTime.TryParse(Val(d, "t0"), CultureInfo.InvariantCulture, DateTimeStyles.None, out p.Debut);
        DateTime.TryParse(Val(d, "t1"), CultureInfo.InvariantCulture, DateTimeStyles.None, out p.Cible);
        return p;
    }

    static string Val(Dictionary<string, string> d, string k)
    {
        string v;
        return d.TryGetValue(k, out v) ? v : "";
    }

    public string Xml(string exe)
    {
        string trig;
        if (Repeter)
        {
            var jours = new StringBuilder();
            for (int i = 0; i < 7; i++) if (Jours[i] == '1') jours.Append("<" + JoursEn[i] + "/>");
            trig = "<CalendarTrigger><StartBoundary>" + DateTime.Today.Add(TimeSpan.Parse(Heure)).ToString("s") + "</StartBoundary>"
                 + "<ScheduleByWeek><DaysOfWeek>" + jours + "</DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek></CalendarTrigger>";
        }
        else
            trig = "<TimeTrigger><StartBoundary>" + Cible.ToString("s") + "</StartBoundary><EndBoundary>" + Cible.AddMinutes(10).ToString("s") + "</EndBoundary></TimeTrigger>";

        bool reveil = Kind == "on";
        // StartWhenAvailable reste à false : un arrêt manqué (PC en veille) ne doit pas se déclencher au réveil.
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
             + "<RegistrationInfo><Description>" + SecurityElement.Escape(Meta()) + "</Description></RegistrationInfo>"
             + "<Triggers>" + trig + "</Triggers>"
             + "<Settings><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>"
             + "<WakeToRun>" + (reveil ? "true" : "false") + "</WakeToRun><Priority>4</Priority>"
             + (Repeter ? "" : "<DeleteExpiredTaskAfter>PT0S</DeleteExpiredTaskAfter>") + "</Settings>"
             + "<Actions><Exec><Command>" + SecurityElement.Escape(exe) + "</Command><Arguments>" + (reveil ? "--wake" : "--run " + Action) + "</Arguments></Exec></Actions></Task>";
    }
}

static class Planif
{
    public static string Exe { get { return Assembly.GetExecutingAssembly().Location; } }

    static dynamic Service()
    {
        dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        s.Connect();
        return s;
    }

    static dynamic Dossier()
    {
        dynamic s = Service();
        try { return s.GetFolder("\\Sablier"); }
        catch (Exception) { return s.GetFolder("\\").CreateFolder("Sablier"); }
    }

    // Désinstallation : toutes les tâches puis le dossier \Sablier
    public static void ToutSupprimer()
    {
        dynamic s = Service();
        dynamic f;
        try { f = s.GetFolder("\\Sablier"); } catch (Exception) { return; }
        var noms = new List<string>();
        foreach (dynamic t in f.GetTasks(1)) noms.Add((string)t.Name);
        foreach (string n in noms) f.DeleteTask(n, 0);
        s.GetFolder("\\").DeleteFolder("Sablier", 0);
    }

    public static List<Prog> Liste()
    {
        var res = new List<Prog>();
        dynamic f = Dossier();
        foreach (dynamic t in f.GetTasks(1))
        {
            Prog p = Prog.Parse((string)t.Definition.RegistrationInfo.Description);
            if (p == null) continue;
            p.Nom = t.Name;
            p.Actif = t.Enabled;
            p.Prochain = t.NextRunTime;
            // Tâche créée depuis un autre emplacement : repointée vers la copie installée (jamais vers une copie de test)
            if (Setup.Installe)
                try
                {
                    if (!string.Equals((string)t.Definition.Actions[1].Path, Exe, StringComparison.OrdinalIgnoreCase))
                    { Enregistrer(p); if (!p.Actif) Activer(p.Nom, false); }
                }
                catch (Exception) { } // tâche modifiée à la main : on la laisse telle quelle
            res.Add(p);
        }
        return res;
    }

    public static void Enregistrer(Prog p)
    {
        Dossier().RegisterTask(p.Nom, p.Xml(Exe), 6, null, null, 3); // créer ou remplacer, session ouverte de l'utilisateur
    }

    public static void Supprimer(string nom) { Dossier().DeleteTask(nom, 0); }

    public static void Activer(string nom, bool on) { Dossier().GetTask(nom).Enabled = on; }

    // Allumage programmé le plus proche, ou null
    public static Prog ProchainReveil(List<Prog> progs)
    {
        Prog r = null;
        foreach (var p in progs)
            if (p.Kind == "on" && p.Actif && p.Prochain > DateTime.Now && (r == null || p.Prochain < r.Prochain)) r = p;
        return r;
    }

    public static Prog Minuterie(int minutes, string action)
    {
        // calcul en UTC : une minuterie qui traverse le changement d'heure dure bien « minutes »
        var p = new Prog { Nom = "Minuterie", Kind = "timer", Action = action, Heure = "", Debut = DateTime.Now, Cible = DateTime.UtcNow.AddMinutes(minutes).ToLocalTime() };
        Enregistrer(p);
        return p;
    }
}

// ---------- Alimentation ----------

static class Power
{
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    public static void Executer(string action)
    {
        switch (action)
        {
            // sans /f : une appli avec un travail non enregistré peut bloquer l'arrêt plutôt que de le perdre
            case "shutdown": Lancer("shutdown", "/s /t 0"); break;
            case "restart": Lancer("shutdown", "/r /t 0"); break;
            case "sleep": SetSuspendState(false, false, false); break;
            // hibernation désactivée sur ce PC (powercfg /h off) : simple veille
            case "hibernate": if (!SetSuspendState(true, false, false)) SetSuspendState(false, false, false); break;
        }
    }

    public static string Lancer(string exe, string args)
    {
        using (var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
        {
            string sortie = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return sortie;
        }
    }

    // Valeur d'un réglage du mode d'alimentation actif, secteur puis batterie :
    // seules valeurs en 0x… de la sortie de powercfg, quelle que soit la langue de Windows
    static int[] Valeurs(string sousGroupe, string reglage)
    {
        MatchCollection v = Regex.Matches(Lancer("powercfg", "/qh SCHEME_CURRENT " + sousGroupe + " " + reglage), "0x([0-9a-fA-F]{8})");
        var res = new int[v.Count];
        for (int i = 0; i < v.Count; i++) res[i] = Convert.ToInt32(v[i].Groups[1].Value, 16);
        return res;
    }

    // Action du bouton d'alimentation : 0 rien, 1 veille, 2 hibernation, 3 arrêt, 4 écran
    public static int[] ValeursBouton() { return Valeurs("SUB_BUTTONS", "PBUTTONACTION"); }

    public static void Synchroniser()
    {
        try { Bouton(Planif.ProchainReveil(Planif.Liste()) != null); } catch (Exception) { }
    }

    // Tant qu'un allumage est prévu, le bouton d'alimentation hiberne au lieu d'arrêter (sinon le PC ne pourrait pas se rallumer).
    // Sablier ne touche qu'un bouton réglé sur « Arrêter », et le remet ensuite comme il l'a trouvé.
    // shortcut: seul le mode d'alimentation actif est modifié ; si l'utilisateur change de mode pendant un allumage prévu,
    // l'ancien mode garde « Hibernation » — mémoriser le GUID du mode dans le marqueur si ça devient gênant.
    public static void Bouton(bool hiberner)
    {
        int[] v = ValeursBouton();
        string[] modes = { "ac", "dc" };
        using (RegistryKey cle = Registry.CurrentUser.CreateSubKey(@"Software\Sablier"))
            for (int i = 0; i < modes.Length && i < v.Length; i++)
            {
                string marque = "Bouton-" + modes[i];
                if (hiberner && v[i] == 3)
                {
                    Lancer("powercfg", "/set" + modes[i] + "valueindex SCHEME_CURRENT SUB_BUTTONS PBUTTONACTION 2");
                    cle.SetValue(marque, 1);
                }
                else if (!hiberner && cle.GetValue(marque) != null)
                {
                    if (v[i] == 2) Lancer("powercfg", "/set" + modes[i] + "valueindex SCHEME_CURRENT SUB_BUTTONS PBUTTONACTION 3");
                    cle.DeleteValue(marque);
                }
            }
        Lancer("powercfg", "/setactive SCHEME_CURRENT");
    }

    // Réveil par minuteur : Windows rendort le PC après 2 min sans activité ; on le garde allumé jusqu'au retour de l'utilisateur (2 h max)
    public static void ResterEveille()
    {
        uint debut = DerniereSaisie(); // avant tout : une saisie pendant la synchro compte déjà comme un retour
        SetThreadExecutionState(0x80000000 | 0x1); // ES_CONTINUOUS | ES_SYSTEM_REQUIRED
        SetThreadExecutionState(0x1 | 0x2);        // + allume l'écran une fois
        Synchroniser(); // allumage unique passé : plus rien de prévu → le bouton d'alimentation redevient « Arrêter »
        for (int i = 0; i < 7200 && DerniereSaisie() == debut; i++) Thread.Sleep(1000);
    }

    static uint DerniereSaisie()
    {
        var l = new LASTINPUTINFO { cbSize = 8 };
        GetLastInputInfo(ref l);
        return l.dwTime;
    }

    // Le réveil exige « Autoriser les minuteurs de sortie de veille » (sur secteur) dans le mode d'alimentation actif ;
    // la valeur d'origine est notée pour être remise à la désinstallation
    public static void AutoriserReveil()
    {
        int[] v = Valeurs("SUB_SLEEP", "RTCWAKE");
        if (v.Length == 0 || v[0] == 1) return;
        using (RegistryKey cle = Registry.CurrentUser.CreateSubKey(@"Software\Sablier"))
            if (cle.GetValue("Reveil-ac") == null) cle.SetValue("Reveil-ac", v[0]);
        Lancer("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE 1");
        Lancer("powercfg", "/setactive SCHEME_CURRENT");
    }

    public static void RestaurerReveil()
    {
        using (RegistryKey cle = Registry.CurrentUser.OpenSubKey(@"Software\Sablier"))
        {
            object v = cle == null ? null : cle.GetValue("Reveil-ac");
            if (v == null) return;
            Lancer("powercfg", "/setacvalueindex SCHEME_CURRENT SUB_SLEEP RTCWAKE " + v);
            Lancer("powercfg", "/setactive SCHEME_CURRENT");
        }
    }
}

// ---------- Interface : outils communs ----------

static class Ui
{
    // Anglais sur tout Windows non francophone ; SABLIER_LANG=fr|en force la langue
    public static readonly bool En = (Environment.GetEnvironmentVariable("SABLIER_LANG") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) != "fr";
    public static string T(string fr, string en) { return En ? en : fr; }

    static readonly bool Master = (Environment.MachineName + Environment.UserName).ToUpperInvariant().Contains("MASTER");
    static readonly string A1 = Master ? "F43F5E" : "22D3EE", A2 = Master ? "FB923C" : "A78BFA";
    public static readonly CultureInfo Culture = new CultureInfo(En ? "en-GB" : "fr-FR");
    static readonly string[] Abr = En ? new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" } : new[] { "Lun", "Mar", "Mer", "Jeu", "Ven", "Sam", "Dim" };

    // Textes des fenêtres XAML (valeurs exactes des attributs Text, Content, ToolTip, Title, AutomationProperties.Name)
    static readonly Dictionary<string, string> Anglais = new Dictionary<string, string>
    {
        { "Réduire", "Minimize" }, { "Fermer", "Close" }, { "Annuler", "Cancel" }, { "Extinction", "Shutdown" }, { "Allumage", "Wake-up" }, { "Programmes", "Schedules" },
        { "DANS", "IN" }, { "À HEURE FIXE", "AT A SET TIME" }, { "UNE FOIS", "ONCE" }, { "CHAQUE SEMAINE", "EVERY WEEK" },
        { "Diminuer la durée", "Decrease duration" }, { "Augmenter la durée", "Increase duration" },
        { "Reculer de 15 min", "Back 15 min" }, { "Avancer de 15 min", "Forward 15 min" }, { "Heures", "Hours" },
        { "Arrêt", "Shut down" }, { "Redémarrer", "Restart" }, { "Veille", "Sleep" }, { "Hibernation", "Hibernate" },
        { "Lancer la minuterie", "Start timer" }, { "Programmer l'allumage", "Schedule wake-up" },
        { "Activer / mettre en pause", "Enable / pause" }, { "Activer le programme", "Enable schedule" },
        { "Supprimer", "Delete" }, { "Supprimer le programme", "Delete schedule" },
        { "Rien de prévu", "Nothing scheduled" },
        { "Programmez une extinction ou un allumage depuis les onglets Extinction et Allumage.", "Schedule a shutdown or a wake-up from the Shutdown and Wake-up tabs." },
        { "Éteindre", "Turn off" },
        { "Le PC doit être en veille prolongée pour se rallumer : éteignez-le avec « Éteindre » de Sablier ou le bouton du PC, pas avec Démarrer › Arrêter.",
          "To wake up, the PC must be hibernating: turn it off with Sablier's 'Turn off' or the PC's power button, not with Start › Shut down." },
        { "En veille prolongée : allumage prévu", "Hibernates: wake-up scheduled" }, { "Veille prolongée : l'allumage prévu aura lieu", "Hibernate: the scheduled wake-up will happen" },
        { "Jour précédent", "Previous day" }, { "Jour suivant", "Next day" }, { "Plus tard", "Later" }, { "Éteindre maintenant", "Turn off now" },
        { "Pensez à enregistrer votre travail", "Save your work" }, { "secondes", "seconds" }, { "Maintenant", "Now" },
        { "Installer Sablier", "Install Sablier" }, { "Minuterie d'arrêt et allumage programmé", "Shutdown timer and scheduled wake-up" },
        { "Éteindre dans 30 min, 1 h, 2 h…", "Shut down in 30 min, 1 h, 2 h…" }, { "Éteindre ou allumer le PC chaque semaine", "Turn your PC off or on every week" },
        { "Toujours 60 s pour annuler avant l'arrêt", "Always 60 s to cancel before shutdown" }, { "Raccourci sur le Bureau", "Desktop shortcut" },
        { "Installer", "Install" }, { "Pour cette session Windows · sans droits administrateur", "For this Windows user · no admin rights" },
    };

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref PT p);
    struct PT { public int X, Y; }

    public static T Load<T>(string nom)
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(nom))
        using (var r = new System.IO.StreamReader(s))
        {
            string x = r.ReadToEnd().Replace("%A1%", A1).Replace("%A2%", A2);
            if (En) x = Regex.Replace(x, "((?:Text|Content|ToolTip|Title|AutomationProperties\\.Name)=\")([^\"]+)\"", m =>
            {
                string en;
                return Anglais.TryGetValue(m.Groups[2].Value.Replace("&quot;", "\""), out en) ? m.Groups[1].Value + SecurityElement.Escape(en) + "\"" : m.Value;
            });
            return (T)XamlReader.Parse(x);
        }
    }

    public static void Chrome(Window w)
    {
        w.SourceInitialized += delegate
        {
            IntPtr h = new WindowInteropHelper(w).Handle;
            int sombre = 1, rond = 2;
            DwmSetWindowAttribute(h, 20, ref sombre, 4); // bordure sombre
            DwmSetWindowAttribute(h, 33, ref rond, 4);   // coins arrondis Windows 11
            SetWindowLong(h, -16, GetWindowLong(h, -16) & ~0x10000); // sans WS_MAXIMIZEBOX : le double-clic sur le titre n'agrandit plus
            // Un clic sur la barre de titre fige l'appli jusqu'à 500 ms (Windows guette un glissement) : un faux mouvement de souris lève l'attente
            HwndSource.FromHwnd(h).AddHook((IntPtr hw, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == 0xA1 && wp.ToInt64() == 2) // WM_NCLBUTTONDOWN sur HTCAPTION
                {
                    long v = lp.ToInt64();
                    var pt = new PT { X = (short)(v & 0xFFFF), Y = (short)((v >> 16) & 0xFFFF) };
                    ScreenToClient(hw, ref pt);
                    PostMessage(hw, 0x200, IntPtr.Zero, (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)));
                }
                return IntPtr.Zero;
            });
        };
        w.StateChanged += delegate { if (w.WindowState == WindowState.Maximized) w.WindowState = WindowState.Normal; };
        // Fenêtre réduite pour s'adapter à l'écran (au-dessus de la barre des tâches) : ≈ 2/3 de la hauteur, jamais plus que prévu
        double dispo = SystemParameters.WorkArea.Height - 16;
        double k = Math.Min(dispo / w.Height, Math.Max(0.7, Math.Min(1.0, SystemParameters.WorkArea.Height * 0.66 / w.Height)));
        if (k < 1)
        {
            ((FrameworkElement)w.Content).LayoutTransform = new ScaleTransform(k, k);
            w.MinWidth = w.Width = w.MaxWidth = w.Width * k;
            w.MinHeight = w.Height = w.MaxHeight = w.Height * k;
        }
    }

    public static void Anim(DependencyObject o, DependencyProperty p, double to, int ms, double from = double.NaN)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase() };
        if (!double.IsNaN(from)) a.From = from;
        ((IAnimatable)o).BeginAnimation(p, a);
    }

    // Arc de progression depuis le haut, sens horaire ; renvoie la position de son extrémité
    public static Point Arc(Path path, double p, double c, double r)
    {
        p = Math.Max(0, Math.Min(0.9999, p));
        double a = p * 2 * Math.PI;
        var fin = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = new Point(c, c - r) };
        fig.Segments.Add(new ArcSegment(fin, new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true));
        path.Data = new PathGeometry(new[] { fig });
        return fin;
    }

    public static string Nom(string a)
    {
        switch (a)
        {
            case "shutdown": return T("Arrêt", "Shutdown");
            case "restart": return T("Redémarrage", "Restart");
            case "sleep": return T("Veille", "Sleep");
            case "hibernate": return T("Hibernation", "Hibernation");
            case "wake": return T("Allumage", "Wake-up");
            case "auto": return T("Extinction", "Power off");
            default: return a;
        }
    }

    public static string Avant(string a)
    {
        switch (a)
        {
            case "shutdown": return T("avant l'arrêt", "until shutdown");
            case "restart": return T("avant le redémarrage", "until restart");
            case "sleep": return T("avant la veille", "until sleep");
            case "hibernate": return T("avant l'hibernation", "until hibernation");
            case "wake": return T("avant l'allumage", "until wake-up");
            case "auto": return T("avant l'extinction", "until power off");
            default: return "";
        }
    }

    public static string Va(string a)
    {
        switch (a)
        {
            case "shutdown": return T("va s'éteindre", "is shutting down");
            case "restart": return T("va redémarrer", "is restarting");
            case "sleep": return T("va se mettre en veille", "is going to sleep");
            case "hibernate": return T("va hiberner", "is hibernating");
            default: return "· " + a;
        }
    }

    public static string Glyph(string a)
    {
        switch (a)
        {
            case "restart": return "";
            case "sleep": return "";
            case "hibernate": return "";
            case "wake": return "";
            default: return "";
        }
    }

    public static string Jour(DateTime d)
    {
        if (d.Date == DateTime.Today) return T("aujourd'hui", "today");
        if (d.Date == DateTime.Today.AddDays(1)) return T("demain", "tomorrow");
        return d.ToString("ddd d MMM", Culture);
    }

    public static string Quand(DateTime d) { return Jour(d) + T(" à ", " at ") + d.ToString("HH:mm", Culture); }

    public static string Reste(TimeSpan t)
    {
        t = TimeSpan.FromSeconds(Math.Ceiling(t.TotalSeconds));
        if (t.TotalDays >= 1) return string.Format(T("{0} j {1} h", "{0} d {1} h"), (int)t.TotalDays, t.Hours);
        if (t.TotalHours >= 1) return string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        return string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
    }

    public static string Jours(string j)
    {
        if (j == "1111111") return T("Tous les jours", "Every day");
        if (j == "1111100") return T("En semaine", "Weekdays");
        if (j == "0000011") return T("Le week-end", "Weekends");
        var l = new List<string>();
        for (int i = 0; i < 7; i++) if (j[i] == '1') l.Add(Abr[i]);
        return string.Join(", ", l);
    }
}

// ---------- Fenêtre principale ----------

class Fenetre
{
    public readonly Window W;
    List<Prog> progs = new List<Prog>();
    Prog reveil;
    bool? bouton;   // dernier état appliqué au bouton d'alimentation
    string signature;
    int minutes = 60, h, m, ticks, onglet;
    // heure de chaque onglet (0 Extinction, 1 Allumage, 2 Programmes) : chaque onglet garde la sienne
    int[] hs = { (DateTime.Now.Hour + 1) % 24, 7, 0 }, mns = { 0, 0, 0 };
    DateTime jour;  // jour de l'allumage « une fois »
    bool? idle;
    Storyboard pouls;
    readonly TextBlock heroBig, heroSub, heroInfo, duree, goSub, onSub, sheetTexte, snackText;
    readonly TextBox hh, mm, ehh, emm;
    readonly FrameworkElement heroActions, snack;
    readonly UIElement btnAnnuler, btnPlus15, btnEteindre;
    readonly Forms.NotifyIcon tray;
    DispatcherTimer timer;
    Storyboard ambiance;
    bool demarre, charge, quitter, bulleVue; // charge : la liste a été lue au moins une fois

    static readonly string[] Panneaux = { "PanelExt", "PanelOn", "PanelProg" };

    T F<T>(string nom) where T : class { return (T)W.FindName(nom); }

    bool ModeFixe { get { return F<RadioButton>("ModeFixe").IsChecked == true; } }
    bool ModeChaque { get { return F<RadioButton>("ModeChaque").IsChecked == true; } }

    // cache : démarre dans la zone de notification ; signal : évènement « réafficher » de la première instance
    public Fenetre(bool cache, EventWaitHandle signal)
    {
        W = Ui.Load<Window>("Main.xaml");
        Ui.Chrome(W);
        pouls = (Storyboard)W.Resources["Pouls"];
        btnAnnuler = F<UIElement>("BtnCancelTimer");
        btnPlus15 = F<UIElement>("BtnPlus15");
        btnEteindre = F<UIElement>("BtnEteindre");
        heroBig = F<TextBlock>("HeroBig");
        heroSub = F<TextBlock>("HeroSub");
        heroInfo = F<TextBlock>("HeroInfo");
        heroActions = F<FrameworkElement>("HeroActions");
        duree = F<TextBlock>("Duree");
        goSub = F<TextBlock>("GoSub");
        onSub = F<TextBlock>("OnSub");
        sheetTexte = F<TextBlock>("GuideTexte");
        snack = F<FrameworkElement>("Snack");
        snackText = F<TextBlock>("SnackText");
        hh = F<TextBox>("HH");
        mm = F<TextBox>("MM");
        ehh = F<TextBox>("EHH");
        emm = F<TextBox>("EMM");
        jour = Prog.Prochaine(new TimeSpan(7, 0, 0), "0000000").Date;

        F<Button>("BtnMin").Click += delegate { W.WindowState = WindowState.Minimized; };
        F<Button>("BtnClose").Click += delegate { Ranger(); };
        F<RadioButton>("TabExt").Checked += delegate { Onglet(0); };
        F<RadioButton>("TabOn").Checked += delegate { Onglet(1); };
        F<RadioButton>("TabProg").Checked += delegate { Onglet(2); };

        // Extinction : minuterie « Dans », ou extinction à heure fixe
        F<Button>("BtnMoins").Click += delegate { Duree(minutes > 15 ? Math.Max(15, minutes - 15) : minutes - 5); };
        F<Button>("BtnPlus").Click += delegate { Duree(minutes < 15 ? minutes + 5 : minutes + 15); };
        F<UIElement>("DureeCard").MouseWheel += (s, e) => Duree(minutes + (e.Delta > 0 ? 5 : -5));
        foreach (Button b in F<Panel>("Presets").Children) b.Click += (s, e) => Duree(int.Parse((string)((Button)s).Tag));
        F<RadioButton>("ModeDans").Checked += delegate { Maj(); };
        F<RadioButton>("ModeFixe").Checked += delegate { Maj(); };
        F<ButtonBase>("ExtMoins").Click += delegate { Heure(h, m - 15); };
        F<ButtonBase>("ExtPlus").Click += delegate { Heure(h, m + 15); };
        F<Button>("BtnGo").Click += delegate { LancerExtinction(); };
        F<Button>("BtnCancelTimer").Click += delegate { Essayer(() => Planif.Supprimer("Minuterie"), Ui.T("Minuterie annulée", "Timer cancelled")); };
        F<Button>("BtnPlus15").Click += delegate
        {
            Prog t = progs.Find(p => p.Nom == "Minuterie");
            if (t != null) Essayer(() => { t.Cible = t.Cible.AddMinutes(15); Planif.Enregistrer(t); }, Ui.T("Minuterie prolongée de 15 min", "Timer extended by 15 min"));
        };
        F<Button>("BtnEteindre").Click += delegate { EteindreMaintenant(); };

        // Allumage : une fois à une date, ou chaque semaine
        F<ButtonBase>("OnMoins").Click += delegate { Heure(h, m - 15); };
        F<ButtonBase>("OnPlus").Click += delegate { Heure(h, m + 15); };
        F<RadioButton>("ModeUne").Checked += delegate { Maj(); };
        F<RadioButton>("ModeChaque").Checked += delegate { Maj(); };
        F<Button>("DPrev").Click += delegate { jour = jour.AddDays(-1) < DateTime.Today ? DateTime.Today : jour.AddDays(-1); Maj(); };
        F<Button>("DNext").Click += delegate { jour = jour.AddDays(1); Maj(); };
        F<Button>("BtnAllumage").Click += delegate { ProgrammerAllumage(); };
        foreach (string g in new[] { "JoursExt", "JoursOn" })
            foreach (ToggleButton t in F<Panel>(g).Children) t.Click += delegate { Maj(); };
        if (Ui.En) // initiales des jours, lundi → dimanche
            foreach (string g in new[] { "JoursExt", "JoursOn" })
            {
                int i = 0;
                foreach (ToggleButton t in F<Panel>(g).Children) t.Content = "MTWTFSS"[i++].ToString();
            }

        // Programmes
        var liste = F<ItemsControl>("Liste");
        RoutedEventHandler bascule = (s, e) =>
        {
            var t = e.OriginalSource as ToggleButton;
            var p = t == null ? null : t.Tag as Prog;
            if (p == null || p.Actif == (t.IsChecked == true)) return; // ignore la mise à jour faite par le binding
            p.Actif = t.IsChecked == true;
            Essayer(() => Planif.Activer(p.Nom, p.Actif), p.Actif ? Ui.T("Programme activé", "Schedule enabled") : Ui.T("Programme en pause", "Schedule paused"));
        };
        liste.AddHandler(ToggleButton.CheckedEvent, bascule);
        liste.AddHandler(ToggleButton.UncheckedEvent, bascule);
        liste.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
        {
            var b = e.OriginalSource as Button; // ToggleButton n'est pas un Button : seul le bouton corbeille passe
            var p = b == null ? null : b.Tag as Prog;
            if (p != null) Essayer(() => Planif.Supprimer(p.Nom), Ui.T("Programme supprimé", "Schedule deleted"));
        }));

        // Guide après un allumage (carte qui monte du bas)
        F<UIElement>("SheetDim").MouseLeftButtonDown += delegate { FermerGuide(); };
        F<Button>("GuideTard").Click += delegate { FermerGuide(); };
        F<Button>("GuideOff").Click += delegate { EteindreMaintenant(); FermerGuide(); };
        Saisie(hh, 24, v => h = v, d => Heure(h + d, m));
        Saisie(mm, 60, v => m = v, d => Heure(h, m + 5 * d));
        Saisie(ehh, 24, v => h = v, d => Heure(h + d, m));
        Saisie(emm, 60, v => m = v, d => Heure(h, m + 5 * d));
        h = hs[0]; m = mns[0]; // heure par défaut de l'onglet Extinction (prochaine heure pleine)
        AfficherHeure();

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += delegate
        {
            if (++ticks % 40 == 0) Recharger(); else Tick();
            if (!W.IsVisible && charge && Suivant(DateTime.Now) == null) Quitter(); // caché et plus rien de prévu
        };
        W.Loaded += delegate { Demarrer(); };
        W.Activated += delegate { Recharger(); };

        // Animations d'ambiance en pause quand Sablier n'est pas au premier plan : quasi aucun CPU en arrière-plan
        ambiance = (Storyboard)W.Resources["Ambiance"];
        W.Activated += delegate { ambiance.Resume(W); if (idle == false) pouls.Resume(W); };
        W.Deactivated += delegate { ambiance.Pause(W); if (idle == false) pouls.Pause(W); };

        // Zone de notification : icône, clic gauche ou double-clic = réafficher ; menu clic droit
        tray = new Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Planif.Exe), Text = "Sablier", Visible = cache };
        tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) Afficher(); };
        tray.DoubleClick += delegate { Afficher(); };
        var menu = new Forms.ContextMenuStrip();
        var ouvrir = new Forms.ToolStripMenuItem(Ui.T("Ouvrir Sablier", "Open Sablier"), null, delegate { Afficher(); });
        ouvrir.Font = new System.Drawing.Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold);
        var annuler = new Forms.ToolStripMenuItem(Ui.T("Annuler la minuterie", "Cancel timer"), null, delegate { Essayer(() => Planif.Supprimer("Minuterie"), Ui.T("Minuterie annulée", "Timer cancelled")); });
        var quitterItem = new Forms.ToolStripMenuItem(Ui.T("Quitter", "Quit"), null, delegate { Quitter(); });
        menu.Items.Add(ouvrir);
        menu.Items.Add(annuler);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(quitterItem);
        menu.Opening += (s, e) => { annuler.Visible = progs.Find(p => p.Nom == "Minuterie") != null; };
        tray.ContextMenuStrip = menu;

        W.Closing += (s, e) => { if (!quitter) { e.Cancel = true; Ranger(); } }; // Alt+F4 = fermer dans la zone de notification
        Application.Current.Exit += delegate { tray.Dispose(); };
        // Écoute des demandes de la deuxième instance (thread d'arrière-plan)
        if (signal != null)
            new Thread(new ThreadStart(delegate { while (true) { signal.WaitOne(); W.Dispatcher.BeginInvoke(new Action(Afficher)); } })) { IsBackground = true }.Start();
        if (cache) { new WindowInteropHelper(W).EnsureHandle(); Demarrer(); } // pas de Loaded sans affichage
    }

    void Essayer(Action a, string ok)
    {
        try { a(); Recharger(); if (ok != null) Snack(ok); }
        catch (Exception ex) { signature = null; Recharger(); Snack(Ui.T("Erreur : ", "Error: ") + (ex.InnerException ?? ex).Message); } // liste reconstruite : interrupteurs remis au vrai état
    }

    void Recharger()
    {
        try { progs = Planif.Liste(); }
        catch (Exception ex) { Snack(Ui.T("Planificateur inaccessible : ", "Task Scheduler unavailable: ") + ex.Message); return; }
        charge = true;
        reveil = Planif.ProchainReveil(progs);
        if (bouton != (reveil != null))
        {
            bouton = reveil != null;
            try { Power.Bouton(reveil != null); } catch (Exception) { }
        }
        var liste = progs.FindAll(p => p.Kind != "timer");
        liste.Sort((a, b) => string.CompareOrdinal(a.Heure, b.Heure));
        foreach (var p in liste) p.Hiberne = reveil != null && p.Actif && p.Action == "shutdown";
        // la date du jour reconstruit la liste après minuit (« demain » → « aujourd'hui »)
        string sig = string.Join("|", liste.ConvertAll(p => p.Nom + p.Actif + p.Hiberne).ToArray()) + DateTime.Today.ToString("yyyyMMdd");
        if (sig != signature)
        {
            signature = sig;
            F<ItemsControl>("Liste").ItemsSource = liste;
            F<UIElement>("Vide").Visibility = liste.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Tick();
    }

    void Tick()
    {
        DateTime now = DateTime.Now;
        Prog next = Suivant(now);

        Idle(next == null);
        if (next == null)
        {
            heroBig.Text = now.ToString("HH:mm", Ui.Culture);
            heroSub.Text = Ui.T("Rien de prévu", "Nothing scheduled");
            heroInfo.Text = Ui.T("Ce PC reste allumé", "This PC stays on");
        }
        else
        {
            TimeSpan reste = next.Prochain - now;
            heroBig.Text = Ui.Reste(reste);
            heroSub.Text = Ui.Avant(next.Action);
            heroInfo.Text = (next.Kind == "timer" ? Ui.T("Minuterie · ", "Timer · ") : "") + Ui.Nom(next.Action) + " " + Ui.Quand(next.Prochain);
        }
        // boutons du héros : annuler / +15 min pour une minuterie, éteindre en veille prolongée s'il y a un allumage prévu
        bool minuterie = next != null && next.Kind == "timer";
        bool eteindre = next != null && reveil != null;
        btnAnnuler.Visibility = minuterie ? Visibility.Visible : Visibility.Collapsed;
        btnPlus15.Visibility = minuterie ? Visibility.Visible : Visibility.Collapsed;
        btnEteindre.Visibility = eteindre ? Visibility.Visible : Visibility.Collapsed;
        heroActions.Visibility = minuterie || eteindre ? Visibility.Visible : Visibility.Hidden;
        if (tray.Visible)
        {
            string info = next == null ? "Sablier" : "Sablier · " + Ui.Nom(next.Action) + " " + Ui.T("dans ", "in ") + Ui.Reste(next.Prochain - now);
            tray.Text = info.Length > 63 ? info.Substring(0, 63) : info; // NotifyIcon.Text : 63 caractères au plus
        }
        Maj();
    }

    // Prochain programme ou minuterie à venir, ou null
    Prog Suivant(DateTime now)
    {
        Prog next = null;
        foreach (var p in progs)
            if (p.Actif && p.Prochain > now && (next == null || p.Prochain < next.Prochain)) next = p;
        return next;
    }

    // Affichage initial : au Loaded, ou tout de suite si Sablier démarre caché
    void Demarrer()
    {
        if (demarre) return;
        demarre = true;
        Duree(60); Recharger(); timer.Start();
        ambiance.Begin(W, true); if (!W.IsActive) ambiance.Pause(W);
    }

    void Afficher()
    {
        W.Show();
        if (W.WindowState == WindowState.Minimized) W.WindowState = WindowState.Normal;
        W.Activate();
        tray.Visible = false;
    }

    // Fermer la fenêtre : Sablier reste dans la zone de notification si quelque chose est prévu
    void Ranger()
    {
        if (Suivant(DateTime.Now) == null) { W.Dispatcher.BeginInvoke(new Action(Quitter)); return; } // différé : on est dans Closing
        W.Hide();
        tray.Visible = true;
        if (!bulleVue)
        {
            bulleVue = true;
            tray.ShowBalloonTip(4000, "Sablier", Ui.T("Sablier reste ici tant qu'une extinction ou un allumage est prévu.", "Sablier stays here while a shutdown or wake-up is scheduled."), Forms.ToolTipIcon.None);
        }
    }

    void Quitter()
    {
        quitter = true;
        tray.Visible = false;
        tray.Dispose();
        Application.Current.Shutdown();
    }

    // Cercle plein : il pulse lentement seulement quand quelque chose est prévu
    void Idle(bool on)
    {
        if (idle == on) return;
        idle = on;
        if (on) pouls.Stop(W);
        else { pouls.Begin(W, true); if (!W.IsActive) pouls.Pause(W); }
    }

    // Fenêtre de 10 s avant l'extinction ; hiberne car un allumage est prévu
    void EteindreMaintenant()
    {
        try { Process.Start(Planif.Exe, "--off"); }
        catch (Exception ex) { Snack(Ui.T("Erreur : ", "Error: ") + ex.Message); }
    }

    // Trois onglets : Extinction (0), Allumage (1), Programmes (2)
    void Onglet(int i)
    {
        if (i == onglet) return;
        hs[onglet] = h; mns[onglet] = m; // chaque onglet garde son heure
        int sens = i > onglet ? 1 : -1;
        onglet = i;
        h = hs[i]; m = mns[i];
        AfficherHeure();
        var show = F<FrameworkElement>(Panneaux[i]);
        for (int k = 0; k < Panneaux.Length; k++) if (k != i) F<FrameworkElement>(Panneaux[k]).Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;
        Ui.Anim(show, UIElement.OpacityProperty, 1, 300, 0);
        Ui.Anim(show.RenderTransform, TranslateTransform.XProperty, 0, 350, 24 * sens);
        var pilule = F<FrameworkElement>("TabPill");
        Ui.Anim(pilule.RenderTransform, TranslateTransform.XProperty, i * pilule.Width, 350);
    }

    void Duree(int v)
    {
        minutes = Math.Max(5, Math.Min(24 * 60, v));
        duree.Text = minutes < 60 ? minutes + " min" : minutes / 60 + " h" + (minutes % 60 == 0 ? "" : " " + (minutes % 60).ToString("00"));
        var s = (ScaleTransform)duree.RenderTransform;
        Ui.Anim(s, ScaleTransform.ScaleXProperty, 1, 260, 1.08);
        Ui.Anim(s, ScaleTransform.ScaleYProperty, 1, 260, 1.08);
        Tick();
    }

    string Choix(string grille)
    {
        foreach (RadioButton r in F<Panel>(grille).Children) if (r.IsChecked == true) return (string)r.Tag;
        return "shutdown";
    }

    // Jours cochés d'une rangée (lundi → dimanche)
    string JoursCoches(string grille)
    {
        var sb = new StringBuilder();
        foreach (ToggleButton t in F<Panel>(grille).Children) sb.Append(t.IsChecked == true ? '1' : '0');
        return sb.ToString();
    }

    // Champ HH ou MM : chiffres seulement, molette et flèches haut/bas
    void Saisie(TextBox box, int max, Action<int> set, Action<int> pas)
    {
        box.PreviewTextInput += (s, e) => e.Handled = e.Text.Length == 0 || !char.IsDigit(e.Text, 0);
        box.TextChanged += delegate { int v; if (int.TryParse(box.Text, out v) && v >= 0 && v < max) { set(v); Maj(); } };
        box.LostKeyboardFocus += delegate { AfficherHeure(); };
        box.GotKeyboardFocus += delegate { box.SelectAll(); };
        box.PreviewMouseWheel += (s, e) => { pas(e.Delta > 0 ? 1 : -1); e.Handled = true; };
        box.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Up) { pas(1); e.Handled = true; }
            if (e.Key == Key.Down) { pas(-1); e.Handled = true; }
        };
    }

    // Règle l'heure en reportant les minutes sur les heures (23:50 + 15 min = 00:05)
    void Heure(int nh, int nm)
    {
        int t = Prog.MinutesDansJour(nh * 60 + nm);
        h = t / 60; m = t % 60;
        AfficherHeure();
        Maj();
    }

    void AfficherHeure()
    {
        hh.Text = ehh.Text = h.ToString("00");
        mm.Text = emm.Text = m.ToString("00");
    }

    // Tout ce qui dépend du mode, de l'heure et des jours : textes et boutons des deux onglets
    void Maj()
    {
        MajExtinction();
        MajAllumage();
    }

    void MajExtinction()
    {
        bool fixe = ModeFixe;
        F<UIElement>("DansPanel").Visibility = fixe ? Visibility.Hidden : Visibility.Visible;
        F<UIElement>("FixePanel").Visibility = fixe ? Visibility.Visible : Visibility.Hidden;
        string a = Choix("ActionsTimer");
        string suffixe = a == "shutdown" && reveil != null ? Ui.T(" · en veille prolongée (allumage prévu)", " · hibernates (wake-up scheduled)") : "";
        if (!fixe)
        {
            F<TextBlock>("GoTitre").Text = Ui.T("Lancer la minuterie", "Start timer");
            goSub.Text = Ui.Nom(a) + " " + Ui.Quand(DateTime.Now.AddMinutes(minutes)) + suffixe;
            return;
        }
        string jours = JoursCoches("JoursExt");
        bool rep = jours != "0000000"; // aucun jour coché = une seule fois
        DateTime cible = Prog.Prochaine(new TimeSpan(h, m, 0), rep ? jours : "0000000");
        F<TextBlock>("GoTitre").Text = Ui.T("Programmer l'extinction", "Schedule shutdown");
        goSub.Text = (rep ? Ui.Jours(jours) + Ui.T(" · prochaine fois ", " · next ") : Ui.T("Une fois, ", "Once, ")) + Ui.Quand(cible) + suffixe;
    }

    void MajAllumage()
    {
        bool rep = ModeChaque;
        F<UIElement>("DateRow").Visibility = rep ? Visibility.Hidden : Visibility.Visible;
        F<UIElement>("JoursOnRow").Visibility = rep ? Visibility.Visible : Visibility.Hidden;
        string date = Ui.Jour(jour);
        F<TextBlock>("DateText").Text = char.ToUpper(date[0]) + date.Substring(1);
        string jours = JoursCoches("JoursOn");
        if (rep && jours == "0000000") jours = "1111111"; // chaque semaine sans jour coché = tous les jours
        DateTime cible = CibleAllumage(rep, jours);
        if (!rep && cible <= DateTime.Now) onSub.Text = Ui.T("Heure déjà passée : choisissez un autre jour", "Time already passed: pick another day");
        else onSub.Text = (rep ? Ui.Jours(jours) + Ui.T(" · prochaine fois ", " · next ") : Ui.T("Une fois, ", "Once, ")) + Ui.Quand(cible);
    }

    DateTime CibleAllumage(bool rep, string jours)
    {
        return rep ? Prog.Prochaine(new TimeSpan(h, m, 0), jours) : jour.Add(new TimeSpan(h, m, 0));
    }

    // Bouton de l'onglet Extinction : minuterie « Dans », ou extinction à heure fixe (une fois ou chaque semaine)
    void LancerExtinction()
    {
        string a = Choix("ActionsTimer");
        if (!ModeFixe)
        {
            Essayer(() => Planif.Minuterie(minutes, a), Ui.T("Minuterie lancée · ", "Timer started · ") + Ui.Nom(a) + " " + Ui.Quand(DateTime.Now.AddMinutes(minutes)) + Ui.T(" · vous pouvez fermer Sablier", " · you can close Sablier"));
            return;
        }
        string jours = JoursCoches("JoursExt");
        bool rep = jours != "0000000";
        Prog p = Nouveau("off", a, rep, jours, Prog.Prochaine(new TimeSpan(h, m, 0), rep ? jours : "0000000"));
        Essayer(() => Planif.Enregistrer(p), Ui.T("Extinction programmée · ", "Shutdown scheduled · ") + Ui.Quand(p.Cible) + Ui.T(" · vous pouvez fermer Sablier", " · you can close Sablier"));
    }

    // Allumage : une fois à une date, ou chaque semaine. Le PC doit ensuite être en veille prolongée (guide affiché)
    void ProgrammerAllumage()
    {
        bool rep = ModeChaque;
        string jours = JoursCoches("JoursOn");
        if (rep && jours == "0000000") jours = "1111111";
        DateTime cible = CibleAllumage(rep, jours);
        if (!rep && cible <= DateTime.Now) { Snack(Ui.T("Heure déjà passée : choisissez un autre jour", "Time already passed: pick another day")); return; }
        Prog p = Nouveau("on", "wake", rep, jours, cible);
        Essayer(() => { Power.AutoriserReveil(); Planif.Enregistrer(p); Guide(p); }, null); // la carte guide confirme déjà l'allumage
    }

    // Programme (Allumage ou Extinction à heure fixe) à partir des réglages en cours (h, m)
    Prog Nouveau(string kind, string action, bool rep, string jours, DateTime cible)
    {
        return new Prog
        {
            Nom = "Programme-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"),
            Kind = kind,
            Action = action,
            Heure = h.ToString("00") + ":" + m.ToString("00"),
            Jours = rep ? jours : "0000000",
            Repeter = rep,
            Debut = DateTime.Now,
            Cible = cible
        };
    }

    // Après un allumage : explique comment éteindre pour qu'il ait lieu
    void Guide(Prog p)
    {
        F<UIElement>("Sheet").Visibility = Visibility.Visible;
        F<UIElement>("GuideCard").Visibility = Visibility.Visible;
        Ui.Anim(F<UIElement>("SheetDim"), UIElement.OpacityProperty, 1, 250, 0);
        Ui.Anim(F<FrameworkElement>("GuideCard").RenderTransform, TranslateTransform.YProperty, 0, 420, 640);
        F<TextBlock>("GuideTitre").Text = Ui.T("Allumage prévu ", "Wake-up scheduled ") + Ui.Quand(p.Cible);
        string texte = Ui.T(
            "Pour que le PC se rallume tout seul, il doit être en veille prolongée, pas éteint.\n\n• Éteignez avec « Éteindre » dans Sablier (ou son raccourci « Éteindre le PC ») ou avec le bouton d'alimentation du PC.\n• Les arrêts programmés et minuteries « Arrêt » passent eux aussi en veille prolongée.\n• Évitez Démarrer › Arrêter : l'allumage n'aurait pas lieu.\n\nAu réveil, le PC reste allumé jusqu'à votre retour (2 h max).",
            "To wake up on its own, the PC must be hibernating, not shut down.\n\n• Use Sablier's \"Turn off\" button (or its \"Shut down PC\" shortcut) or the PC's power button.\n• Scheduled shutdowns and shut-down timers hibernate too.\n• Avoid Start › Shut down: the wake-up would not happen.\n\nOnce it wakes up, the PC stays on until you're back (2 h max).");
        if (p.Repeter) texte = Ui.T("Allumage récurrent : " + Ui.Jours(p.Jours) + ".\n\n", "Recurring wake-up: " + Ui.Jours(p.Jours) + ".\n\n") + texte;
        sheetTexte.Text = texte;
    }

    // Ferme le guide (overlay et carte)
    void FermerGuide()
    {
        var carte = F<FrameworkElement>("GuideCard");
        Ui.Anim(F<UIElement>("SheetDim"), UIElement.OpacityProperty, 0, 220);
        var a = new DoubleAnimation(640, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        a.Completed += delegate
        {
            F<UIElement>("Sheet").Visibility = Visibility.Collapsed;
            F<UIElement>("GuideCard").Visibility = Visibility.Collapsed;
        };
        ((TranslateTransform)carte.RenderTransform).BeginAnimation(TranslateTransform.YProperty, a);
    }

    void Snack(string msg)
    {
        snackText.Text = msg;
        int ms = Math.Min(9000, 2600 + 45 * msg.Length); // le temps de lire un message d'erreur long
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms + 400))));
        snack.BeginAnimation(UIElement.OpacityProperty, a);
        Ui.Anim(snack.RenderTransform, TranslateTransform.YProperty, 0, 300, 16);
        var pair = UIElementAutomationPeer.CreatePeerForElement(snackText); // lu par les lecteurs d'écran
        if (pair != null) pair.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}

// ---------- Avertissement de 60 s avant l'action (lancé par la tâche planifiée) ----------

class Alerte
{
    public readonly Window W;
    readonly string action;
    readonly DateTime fin;
    readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };

    public Alerte(string demande, int secondes)
    {
        W = Ui.Load<Window>("Alerte.xaml");
        Ui.Chrome(W);
        var arc = (Path)W.FindName("Arc");
        var sec = (TextBlock)W.FindName("Sec");
        var info = (TextBlock)W.FindName("Info");
        action = demande;
        // « Éteindre le PC » : hibernation si un allumage est prévu (un arrêt complet l'empêcherait), sinon arrêt normal
        if (demande == "auto" || demande == "shutdown")
        {
            Prog reveil = null;
            bool illisible = false;
            try { reveil = Planif.ProchainReveil(Planif.Liste()); } catch (Exception) { illisible = true; }
            // Planificateur illisible : dans le doute on hiberne, un éventuel allumage reste possible (« auto » seulement)
            if (demande == "auto") action = reveil == null && !illisible ? "shutdown" : "hibernate";
            else if (reveil != null) action = "hibernate"; // arrêt programmé : hiberne seulement si un allumage est prévu
            if (reveil != null) info.Text = Ui.T("Hibernation pour que l'allumage prévu " + Ui.Quand(reveil.Prochain) + " ait lieu",
                                                 "Hibernating so the wake-up scheduled " + Ui.Quand(reveil.Prochain) + " can happen");
        }
        ((TextBlock)W.FindName("Titre")).Text = Ui.T("Ce PC ", "This PC ") + Ui.Va(action);
        fin = DateTime.Now.AddSeconds(secondes);
        sec.Text = secondes.ToString();
        timer.Tick += delegate
        {
            double reste = (fin - DateTime.Now).TotalSeconds;
            if (reste <= 0) { Go(); return; }
            sec.Text = Math.Ceiling(reste).ToString();
            Ui.Arc(arc, reste / secondes, 100, 83);
        };
        ((Button)W.FindName("BtnAnnuler")).Click += delegate { W.Close(); };
        ((Button)W.FindName("BtnReporter")).Click += delegate
        {
            try { Planif.Minuterie(15, demande); W.Close(); } // « auto » sera réévalué dans 15 min
            catch (Exception ex) { info.Text = Ui.T("Report impossible : ", "Could not postpone: ") + ex.Message; }
        };
        ((Button)W.FindName("BtnMaintenant")).Click += delegate { Go(); };
        W.Loaded += delegate { timer.Start(); SystemSounds.Exclamation.Play(); };
    }

    void Go()
    {
        timer.Stop();
        W.Hide();
        Power.Executer(action);
        W.Close();
    }
}

// ---------- Installation : Sablier.exe lancé hors de son dossier sert d'installeur (fichier à partager tel quel) ----------

class Setup
{
    const string Version = "1.3";
    const string CleDesinstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Sablier";
    const string CleRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string EteindreFr = "Éteindre le PC.lnk", EteindreEn = "Shut down PC.lnk";
    static readonly string Dossier = IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Sablier");
    static readonly string Cible = IO.Path.Combine(Dossier, "Sablier.exe");
    static readonly string Programmes = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    static readonly string Bureau = IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Sablier.lnk");

    public static bool Installe { get { return string.Equals(Planif.Exe, Cible, StringComparison.OrdinalIgnoreCase); } }

    public readonly Window W;

    public Setup()
    {
        W = Ui.Load<Window>("Setup.xaml");
        Ui.Chrome(W);
        var go = (Button)W.FindName("BtnInstaller");
        var goText = (TextBlock)W.FindName("GoText");
        var statut = (TextBlock)W.FindName("Statut");
        if (IO.File.Exists(Cible)) goText.Text = Ui.T("Mettre à jour", "Update");
        ((Button)W.FindName("BtnClose")).Click += delegate { W.Close(); };
        go.Click += delegate
        {
            go.IsHitTestVisible = false; // pas IsEnabled : le bouton resterait grisé sur « Installé ! »
            try { Installer(((ToggleButton)W.FindName("Bureau")).IsChecked == true); }
            catch (Exception ex) { statut.Text = Ui.T("Échec : ", "Failed: ") + ex.Message; go.IsHitTestVisible = true; return; }
            ((TextBlock)W.FindName("GoIcon")).Text = "";
            goText.Text = Ui.T("Installé !", "Installed!");
            statut.Text = Ui.T("Sablier est dans le menu Démarrer. Ouverture…", "Sablier is in the Start menu. Opening…");
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
            t.Tick += delegate { t.Stop(); Process.Start(Cible); W.Close(); };
            t.Start();
        };
    }

    // Une copie installée encore ouverte bloquerait la copie ou la suppression de l'exe
    static void FermerAutres()
    {
        foreach (Process p in Process.GetProcessesByName("Sablier"))
            try
            {
                if (p.Id != Process.GetCurrentProcess().Id && string.Equals(p.MainModule.FileName, Cible, StringComparison.OrdinalIgnoreCase))
                { p.Kill(); p.WaitForExit(3000); }
            }
            catch (Exception) { }
    }

    static void Installer(bool raccourciBureau)
    {
        IO.Directory.CreateDirectory(Dossier);
        FermerAutres();
        IO.File.Copy(Planif.Exe, Cible, true);

        string description = Ui.T("Minuterie d'arrêt et allumage programmé", "Shutdown timer and scheduled wake-up");
        Raccourci(IO.Path.Combine(Programmes, "Sablier.lnk"), "", Cible + ",0", description);
        Raccourci(IO.Path.Combine(Programmes, Ui.T(EteindreFr, EteindreEn)), "--off", @"%SystemRoot%\System32\shell32.dll,27",
                  Ui.T("Hibernation si un allumage est prévu, sinon arrêt", "Hibernates if a wake-up is scheduled, otherwise shuts down"));
        if (raccourciBureau) Raccourci(Bureau, "", Cible + ",0", description);
        else IO.File.Delete(Bureau);

        // « Applications installées » de Windows (par utilisateur, sans droits admin)
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CleDesinstall))
        {
            k.SetValue("DisplayName", "Sablier");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Sablier");
            k.SetValue("DisplayIcon", Cible);
            k.SetValue("InstallLocation", Dossier);
            k.SetValue("UninstallString", "\"" + Cible + "\" --uninstall");
            k.SetValue("QuietUninstallString", "\"" + Cible + "\" --uninstall --quiet");
            k.SetValue("NoModify", 1);
            k.SetValue("NoRepair", 1);
            k.SetValue("EstimatedSize", (int)(new IO.FileInfo(Cible).Length / 1024));
        }
        // À chaque ouverture de session : remet le bouton d'alimentation d'aplomb (allumage unique expiré PC éteint)
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(CleRun))
            k.SetValue("Sablier", "\"" + Cible + "\" --sync");
    }

    static void Raccourci(string lnk, string args, string icone, string description)
    {
        dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic l = sh.CreateShortcut(lnk);
        l.TargetPath = Cible;
        l.Arguments = args;
        l.WorkingDirectory = Dossier;
        l.IconLocation = icone;
        l.Description = description;
        l.Save();
    }

    public static int Silencieux()
    {
        try { Installer(false); return 0; }
        catch (Exception) { return 1; }
    }

    public static void Desinstaller(bool silencieux)
    {
        if (!silencieux && MessageBox.Show(Ui.T("Désinstaller Sablier et supprimer tous ses programmes d'arrêt et d'allumage ?",
                "Uninstall Sablier and delete all its shutdown and wake-up schedules?"), "Sablier",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        FermerAutres();
        try { Planif.ToutSupprimer(); } catch (Exception) { }
        // avant d'effacer les marqueurs : bouton d'alimentation et minuteurs de réveil remis comme avant Sablier
        try { Power.Bouton(false); } catch (Exception) { }
        try { Power.RestaurerReveil(); } catch (Exception) { }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Sablier", false);
        Registry.CurrentUser.DeleteSubKeyTree(CleDesinstall, false);
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(CleRun, true))
            if (k != null) k.DeleteValue("Sablier", false);
        IO.File.Delete(IO.Path.Combine(Programmes, "Sablier.lnk"));
        IO.File.Delete(IO.Path.Combine(Programmes, EteindreFr));
        IO.File.Delete(IO.Path.Combine(Programmes, EteindreEn));
        IO.File.Delete(Bureau);
        // l'exe ne peut pas s'effacer pendant qu'il tourne : cmd attend sa fermeture puis supprime le dossier
        Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + Dossier + "\"") { CreateNoWindow = true, UseShellExecute = false });
    }
}

// ---------- Auto-test : Sablier.exe --selftest (code de sortie = nombre d'échecs) ----------

static class SelfTest
{
    static int echecs;

    static void Ok(bool c, string quoi)
    {
        Console.WriteLine((c ? "OK     " : "ÉCHEC  ") + quoi);
        if (!c) echecs++;
    }

    public static int Run()
    {
        var h = new TimeSpan(7, 30, 0);
        DateTime n = Prog.Prochaine(h, "0000000");
        Ok(n > DateTime.Now && n <= DateTime.Now.AddDays(1), "prochaine occurrence dans les 24 h");
        int j = ((int)DateTime.Today.DayOfWeek + 6) % 7;
        string seul = "0000000".Remove(j, 1).Insert(j, "1");
        Ok(Prog.Prochaine(TimeSpan.Zero, seul) == DateTime.Today.AddDays(7), "jour unique déjà passé → semaine suivante");
        Ok(Ui.Reste(TimeSpan.FromSeconds(3599.2)) == "1:00:00" && Ui.Reste(TimeSpan.FromSeconds(59)) == "00:59", "format du compte à rebours");
        Ok(Ui.Jour(DateTime.Today.AddDays(1)) == Ui.T("demain", "tomorrow"), "libellé de date");
        Ok(Prog.MinutesDansJour(-15) == 1425 && Prog.MinutesDansJour(1455) == 15, "minutes ramenées dans la journée");

        var p = new Prog { Nom = "Test-Sablier", Kind = "off", Action = "sleep", Heure = "07:30", Jours = "1010100", Repeter = true, Debut = DateTime.Now, Cible = DateTime.Now };
        Prog q = Prog.Parse(p.Meta());
        Ok(q != null && q.Kind == p.Kind && q.Action == p.Action && q.Heure == p.Heure && q.Jours == p.Jours && q.Repeter
           && Math.Abs((q.Debut - p.Debut).TotalSeconds) < 1, "métadonnées aller-retour");

        // Le Planificateur doit accepter chaque forme de tâche et prévoir la même date que l'appli
        Tache(p, Prog.Prochaine(h, p.Jours));
        p.Kind = "on"; p.Action = "wake"; p.Jours = "1111111";
        Tache(p, Prog.Prochaine(h, p.Jours));
        p.Kind = "off"; p.Action = "hibernate"; p.Repeter = false; p.Cible = DateTime.Today.AddDays(2).Add(h);
        Tache(p, p.Cible);
        p.Kind = "timer"; p.Cible = DateTime.Now.AddMinutes(90);
        Tache(p, p.Cible.AddTicks(-(p.Cible.Ticks % TimeSpan.TicksPerSecond)));

        // Bouton d'alimentation : « Arrêter » → hibernation pendant un allumage prévu, puis retour à l'état d'origine
        if (Planif.ProchainReveil(Planif.Liste()) != null) Console.WriteLine("—      bouton d'alimentation : ignoré (un vrai allumage est programmé)");
        else try
        {
            int[] avant = Power.ValeursBouton();
            Power.Bouton(true);
            int[] pendant = Power.ValeursBouton();
            Power.Bouton(false);
            int[] apres = Power.ValeursBouton();
            Ok(avant.Length == 2 && pendant[0] == (avant[0] == 3 ? 2 : avant[0]) && apres[0] == avant[0] && apres[1] == avant[1],
               "bouton d'alimentation " + string.Join("/", Array.ConvertAll(avant, x => x.ToString())) + " → " + string.Join("/", Array.ConvertAll(pendant, x => x.ToString()))
               + " → " + string.Join("/", Array.ConvertAll(apres, x => x.ToString())));
        }
        catch (Exception ex) { Ok(false, "bouton d'alimentation : " + ex.Message); }
        finally { Power.Synchroniser(); } // état réel de l'utilisateur
        return echecs;
    }

    static void Tache(Prog p, DateTime attendu)
    {
        string quoi = p.Kind + "/" + p.Action + "/" + (p.Repeter ? p.Jours : "une fois");
        try
        {
            Planif.Enregistrer(p);
            Prog lu = Planif.Liste().Find(x => x.Nom == p.Nom);
            Ok(lu != null && lu.Actif && lu.Prochain == attendu, quoi + " → " + (lu == null ? "absente" : lu.Prochain.ToString("s")) + " (attendu " + attendu.ToString("s") + ")");
            if (p.Kind == "on") Ok(Planif.ProchainReveil(Planif.Liste()) != null, "allumage prévu détecté");
        }
        catch (Exception ex) { Ok(false, quoi + " : " + ex.Message); }
        finally { try { Planif.Supprimer(p.Nom); } catch (Exception) { } }
    }
}
