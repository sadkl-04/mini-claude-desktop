// Headless check of the interactions: drives the state machine with a fake cursor. The form is never shown.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

partial class BuddyForm
{
    readonly List<string> log = new List<string>();
    int failed;
    string outDir;

    bool peekOk = true;
    void Check3(bool ok) { if (!ok) peekOk = false; }

    void Check(string name, bool ok) { log.Add((ok ? "PASS " : "FAIL ") + name); if (!ok) failed++; }

    void Run(Point c, int ticks, Func<int, Point> move = null)
    {
        for (int i = 0; i < ticks; i++) { frame++; Advance(move != null ? move(i) : c, 0.066); }
    }

    Point Far { get { return new Point(work.Left + 5, work.Top + 5); } }
    Point OnBuddy { get { return new Point((int)x + charW / 2, CharTop() + charH / 2); } }

    void Snap(string name) { lastSig = 0; frameBmp.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png); }

    void Reset() { web = null; away = false; webFast = false; st = St.Idle; queued = null; softFall = false; perch = null; leapTarget = null; folders = new List<DeskFolder>(); toy = null; bugOn = false; bubble = null; parts.Clear(); air = 0; x = work.Left + work.Width / 2; Go(St.Idle, 1e9); Run(Far, 1); }

    static MemoryStream QuietTone(int seconds)
    {
        const int rate = 44100;
        int samples = rate * seconds;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
        w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(samples * 2);
        for (int i = 0; i < samples; i++) w.Write((short)(Math.Sin(i * 2 * Math.PI * 220 / rate) * 32767 * 0.03));
        w.Flush();
        ms.Position = 0;
        return ms;
    }

    public int SelfTest(string dir)
    {
        outDir = dir;
        timer.Stop();
        tray.Visible = false;
        cfg.Quiet = false;
        cfg.Sync = false;
        cfg.Climb = false;
        cfg.Weave = false;

        // Passing over once must not count as petting, wiggling must.
        Reset();
        int px = (int)x;
        Run(Far, 40, i => new Point(px - 40 + i * 5, CharTop() + charH / 2));
        Check("crossing once is not petting", st != St.Pet);
        Reset();
        px = (int)x + charW / 2;
        Run(Far, 30, i => new Point(px + (i % 2 == 0 ? -12 : 12), CharTop() + charH / 2));
        Check("wiggle starts petting", st == St.Pet);
        Check("petting says something", bubble != null);
        Snap("pet");
        Run(Far, 40);
        Check("petting ends", st != St.Pet);

        // Cookie: falls, buddy walks over, eats, toy gone.
        Reset();
        StartToy(false);
        Check("cookie widens window", true);
        Run(Far, 2);
        Check("window wide with toy", wide);
        Snap("cookie_falling");
        double cookieX = toy.X;
        int t = 0;
        while (toy != null && t++ < 300) Run(Far, 1);
        Check("cookie gets eaten", toy == null && st == St.Eat);
        Run(Far, 15);
        Snap("eating");
        Run(Far, 40);
        Check("eating ends", st != St.Eat);
        Run(Far, 2);
        Check("window narrow again", !wide || bugOn);

        // Ball: seven kicks, then done. Never lost outside the window.
        Reset();
        StartToy(true);
        bool leashOk = true, airOk = true;
        t = 0;
        while (toy != null && t++ < 900)
        {
            Run(Far, 1);
            if (toy == null) break;
            if (Math.Abs(toy.X - x) > winW - charW - 5 * u + 1) leashOk = false;
            if (toy.Air < 0 || toy.Air > winH) airOk = false;
            if (t == 60) Snap("ball");
        }
        Check("ball game finishes (" + t + " ticks, " + kicks + " kicks)", toy == null);
        Check("ball stays inside window", leashOk);
        Check("ball stays in air bounds", airOk);

        // Clicking the ball kicks it; clicking the bug squishes it.
        Reset();
        StartToy(true);
        Run(Far, 3);
        Check("click on ball handled", ClickWorld(new Point((int)toy.X + u, work.Bottom - (int)toy.Air - 2 * u)));
        Check("click sends ball up", toy.Vy > 0 && toy.Vx != 0);
        Reset();
        Go(St.Hunt, 15);
        Run(Far, 2);
        Snap("hunt");
        Check("click on bug handled", ClickWorld(new Point((int)bugX + u, work.Bottom - u)));
        Check("bug squished", !bugOn && st == St.Happy);

        // Interrupting a game by throwing the buddy resumes it.
        Reset();
        StartToy(true);
        Run(Far, 3);
        Go(St.Fall, 1e9);
        t = 0;
        while (st == St.Fall && t++ < 100) Run(Far, 1);
        Check("game resumes after fall", st == St.Play);

        // Chase: runs to the cursor, tags it three times, wins.
        Reset();
        StartChase();
        Point target = new Point((int)x + 200, CharTop() + charH / 2);
        double before = x;
        Run(target, 10);
        Check("chase moves toward cursor", x > before);
        Snap("chase");
        t = 0;
        while (st == St.Chase && t++ < 400) Run(new Point((int)x + charW / 2, CharTop() + charH / 2), 1);
        Check("chase is won by tagging (" + tags + ")", tags == 3 && st == St.Happy);
        Reset();
        StartChase();
        t = 0;
        while (st == St.Chase && t++ < 600) Run(Far, 1);
        Check("chase times out", st != St.Chase && tags == 0);
        Check("no jump when cursor is high", air == 0);

        // Dance.
        Reset();
        Go(St.Dance, 7);
        Run(Far, 30);
        Check("dancing lifts off", st == St.Dance);
        Snap("dance");
        Run(Far, 90);
        Check("dance ends on the ground", st != St.Dance && (air == 0 || st == St.Happy));

        // Shake: drag side to side, get dizzy on release even when dropped low.
        Reset();
        Point start = OnBuddy;
        mouseDown = true; downPt = lastCursor = start; grab = new Point(start.X - (int)x, start.Y - CharTop());
        Run(start, 20, i => new Point(start.X + (i % 2 == 0 ? -30 : 30), start.Y));
        Check("shaking detected", st == St.Drag && shaken);
        Snap("shaken");
        mouseDown = false; maxFall = 0; vx = vy = 0; Go(St.Fall, 1e9);
        t = 0;
        while (st == St.Fall && t++ < 100) Run(Far, 1);
        Check("dizzy after shaking", st == St.Dizzy && !shaken);

        // Held still in the air complains once.
        Reset();
        start = OnBuddy;
        mouseDown = true; downPt = lastCursor = start; grab = new Point(start.X - (int)x, start.Y - CharTop());
        Point up = new Point(start.X + 20, start.Y - 200);
        Run(up, 90);
        Check("long hold complains", heldSaid && !shaken);
        mouseDown = false; Go(St.Idle, 1e9); air = 0;

        // Away and back.
        Reset();
        Go(St.Walk, 40);
        CheckAway(10);
        Check("short idle ignored", st == St.Walk && !away);
        CheckAway(301);
        Check("long idle sleeps", st == St.Sleep && away);
        CheckAway(1);
        Check("return wakes", st == St.Happy && !away);
        cfg.Quiet = true;
        Go(St.Sleep, 1e9);
        CheckAway(301);
        CheckAway(1);
        Check("quiet stays asleep", st == St.Sleep);
        cfg.Quiet = false;

        // Quiet mode: petting gives a heart but does not wake.
        cfg.Quiet = true;
        Reset();
        Go(St.Sleep, 1e9);
        bubble = null;
        px = (int)x + charW / 2;
        Run(Far, 30, i => new Point(px + (i % 2 == 0 ? -12 : 12), CharTop() + charH / 2));
        Check("quiet petting does not wake", st == St.Sleep && bubble == null);
        cfg.Quiet = false;

        // Tasting files reads only metadata and leaves the file alone.
        string f = Path.Combine(outDir, "taste me.pdf");
        File.WriteAllBytes(f, new byte[3000]);
        DateTime stamp = File.GetLastWriteTimeUtc(f);
        Reset();
        Taste(new[] { f });
        Check("drop makes him eat", st == St.Eat);
        log.Add("     line: " + bubble);
        Run(Far, 20);
        Snap("taste");
        Check("file untouched", File.Exists(f) && File.GetLastWriteTimeUtc(f) == stamp && new FileInfo(f).Length == 3000);
        log.Add("     folder: " + TasteLine(new[] { outDir }));
        log.Add("     many: " + TasteLine(new[] { f, f, f }));
        log.Add("     missing: " + TasteLine(new[] { @"C:\no\such\thing.exe" }));
        Taste(null);
        Taste(new string[0]);
        Check("empty drop ignored", true);


        // Music: short pings are ignored, steady sound starts a dance, silence ends it.
        Action<float, int> sound = (peak, ticks) => { for (int i = 0; i < ticks; i++) { TrackMusic(peak, 0.066); Run(Far, 1); } };
        cfg.Music = true;
        Reset();
        sound(0f, 60);
        Go(St.Idle, 1e9);
        sound(0.5f, 20);
        sound(0f, 20);
        Check("short ping does not dance", st != St.Dance && !musicOn);
        Go(St.Idle, 1e9);
        sound(0.4f, 50);
        Check("steady sound starts dancing", st == St.Dance && musicDance);
        double topAir = 0;
        int beats = beatCount;
        for (int i = 0; i < 90; i++) { sound(i % 8 == 0 ? 0.9f : 0.3f, 1); topAir = Math.Max(topAir, air); if (i == 40) Snap("music_dance"); }
        Check("bounces on beats (" + (beatCount - beats) + " beats, air " + (int)topAir + ")", beatCount - beats >= 8 && topAir > 0);
        beats = beatCount;
        sound(0.5f, 60);
        Check("flat loud track still gets a pulse (" + (beatCount - beats) + ")", beatCount - beats >= 4);
        HandleClick();
        Check("click interrupts", st == St.Happy);
        sound(0.5f, 25);
        Check("dance resumes after click", st == St.Dance && musicDance);
        sound(0.5f, 3); sound(0f, 10); sound(0.5f, 3);
        Check("short gap keeps dancing", st == St.Dance && musicOn);
        CheckAway(400);
        Check("no sleep while music plays", st == St.Dance && !away);
        sound(0f, 45);
        Check("silence ends dance", st != St.Dance && !musicOn && !musicDance);
        Check("back on the ground", air == 0 || st == St.Happy);
        Reset();
        Go(St.Sleep, 1e9);
        sound(0.4f, 50);
        Check("music wakes him", st == St.Dance);
        sound(0f, 45);
        Reset();
        StartToy(true);
        sound(0.4f, 50);
        Check("music does not interrupt a game", st == St.Play);
        toy = null;
        sound(0f, 45);
        Reset();
        cfg.Quiet = true;
        Go(St.Sleep, 1e9);
        sound(0.4f, 50);
        Check("quiet mode ignores music", st == St.Sleep);
        cfg.Quiet = false;
        sound(0f, 45);
        Reset();
        sound(0.4f, 50);
        cfg.Music = false;
        StopMusic();
        Check("toggle off stops dance", st != St.Dance && !musicOn);
        cfg.Music = true;
        cfg.Save();
        Settings reread = Settings.Load();
        Check("music setting persists", reread.Music);

        // The real meter, with a very quiet tone played by this process.
        var meter = new AudioMeter();
        float baseline = 0, playing = 0;
        for (int i = 0; i < 15; i++) { baseline = Math.Max(baseline, meter.Peak()); System.Threading.Thread.Sleep(66); }
        using (var wav = QuietTone(6))
        using (var player = new System.Media.SoundPlayer(wav))
        {
            player.Play();
            for (int i = 0; i < 60; i++) { playing = Math.Max(playing, meter.Peak()); System.Threading.Thread.Sleep(66); }
            player.Stop();
        }
        log.Add("     real meter: baseline " + baseline + ", with tone " + playing);
        Check("real meter hears the tone", playing > 0.015f);

        // Folders: the real desktop is read, then a fake folder is used so the test does not depend on it.
        List<DeskFolder> real = null;
        var scan = new System.Threading.Thread(() => real = Desktop.Folders());
        scan.SetApartmentState(System.Threading.ApartmentState.MTA);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        scan.Start();
        scan.Join();
        int inside = 0;
        foreach (DeskFolder rf in real) if (bounds.Contains(rf.Box.Location)) inside++;
        log.Add("     real desktop: " + real.Count + " folders, " + inside + " on the primary screen, scan " + sw.ElapsedMilliseconds + " ms");
        Check("real scan returns boxes with size", real.TrueForAll(rf => rf.Box.Width > 0 && rf.Box.Height > 0));

        var folder = new DeskFolder { Name = "Папка с очень длинным названием", Box = new Rectangle(work.Left + 300, work.Top + 200, 76, 90) };
        Reset();
        folders.Add(folder);
        LeapTo(folder);
        Check("leap starts", st == St.Leap);
        Run(Far, 8);
        Snap("leap");
        t = 0;
        while (st == St.Leap && t++ < 60) Run(Far, 1);
        Check("lands on folder", st == St.Perch && perch == folder);
        Check("feet on the folder top", CharTop() + charH == folder.Box.Top);
        Check("centred on the folder", Math.Abs(x + charW / 2.0 - (folder.Box.Left + folder.Box.Width / 2.0)) <= 1);
        Snap("perch");
        double perchY = CharTop();
        Run(Far, 30);
        Check("stays up there", st == St.Perch && CharTop() == perchY);
        HandleClick();
        Run(Far, 2);
        Check("click does not knock him down", st == St.Perch && CharTop() == perchY);
        px = (int)x + charW / 2;
        Run(Far, 30, i => new Point(px + (i % 2 == 0 ? -12 : 12), CharTop() + charH / 2));
        Check("petting on folder keeps him there", st == St.Perch && CharTop() == perchY);
        stateTime = stateLen + 1;
        Run(Far, 1);
        Check("gets bored and jumps down", st == St.Fall && softFall);
        t = 0;
        while (st == St.Fall && t++ < 120) Run(Far, 1);
        Check("lands softly without dizziness", st != St.Fall && st != St.Dizzy && air == 0);

        // A menu action while up there: jumps down first, then does it.
        Reset();
        folders.Add(folder);
        PerchOn(folder);
        Go(St.Coffee, 8);
        Check("floor action waits for the jump", st == St.Fall && queued == St.Coffee);
        t = 0;
        while (st == St.Fall && t++ < 120) Run(Far, 1);
        Check("then does it on the floor", st == St.Coffee && air == 0);

        // Thrown from above onto a folder, and dropped straight onto it.
        Reset();
        folders.Add(folder);
        x = folder.Box.Left;
        air = PerchAir(folder) + 200 * d;
        vx = vy = 0; maxFall = 0;
        Go(St.Fall, 1e9);
        t = 0;
        while (st == St.Fall && t++ < 120) Run(Far, 1);
        Check("throw lands on folder", st == St.Perch && perch == folder);
        Check("released over folder perches", FolderAt(folder.Box.Left + 10, folder.Box.Top + 10) == folder);
        Check("released elsewhere does not", FolderAt(folder.Box.Right + 300, folder.Box.Top + 10) == null);
        Reset();
        folders.Add(folder);
        x = folder.Box.Right + 200;
        air = PerchAir(folder) + 200 * d;
        vx = vy = 0; maxFall = 0;
        Go(St.Fall, 1e9);
        t = 0;
        while (st == St.Fall && t++ < 120) Run(Far, 1);
        Check("falls past a folder it misses", st != St.Perch);

        // Folder dragged away while he stands on it.
        Reset();
        folders.Add(folder);
        Go(St.Drag, 1e9);
        Check("dragging off leaves nothing stale", perch == null);

        // Crawl a whole lap each way.
        Point mid = new Point(work.Left + work.Width / 2, work.Top + work.Height / 2);
        foreach (int way in new[] { 1, -1 })
        {
            Reset();
            x = work.Left + work.Width / 2;
            face = way;
            Go(St.Crawl, 1e9);
            var order = new List<int> { 0 };
            bool insideScreen = true, snapped = false;
            t = 0;
            while (st == St.Crawl && t++ < 6000)
            {
                Run(mid, 1);
                if (st != St.Crawl) break;
                if (crawlEdge != order[order.Count - 1]) order.Add(crawlEdge);
                double halfW = crawlEdge % 2 == 0 ? charW / 2.0 : charH / 2.0, halfH = crawlEdge % 2 == 0 ? charH / 2.0 : charW / 2.0;
                if (crawlX - halfW < work.Left - 1 || crawlX + halfW > work.Right + 1 || crawlY - halfH < work.Top - 1 || crawlY + halfH > work.Bottom + 1) insideScreen = false;
                if (way == 1 && !snapped && crawlEdge != 0 && Math.Abs(crawlY - (work.Top + work.Height / 2)) < 8 && crawlEdge == 1) { Snap("crawl_right"); snapped = true; }
                if (way == 1 && crawlEdge == 2 && Math.Abs(crawlX - (work.Left + work.Width / 2)) < 3) { bubble = "Потолок тоже надо ревьюить"; bubbleAge = 0.5; bubbleLen = 9; Run(mid, 2); Snap("crawl_top"); }
                if (way == 1 && crawlEdge == 3 && Math.Abs(crawlY - (work.Top + work.Height / 2)) < 3) Snap("crawl_left");
            }
            string seq = string.Join(",", order);
            Check("lap dir " + way + " visits " + seq, seq == (way == 1 ? "0,1,2,3,0" : "0,3,2,1,0"));
            Check("lap dir " + way + " stays on screen", insideScreen);
            Check("lap dir " + way + " ends on the floor", st != St.Crawl && air == 0 && t < 6000);
        }

        // Hovering pauses the crawl, so he can be grabbed; dragging off a wall works.
        Reset();
        Go(St.Crawl, 1e9);
        crawlEdge = 1; crawlX = work.Right - charH / 2.0; crawlY = work.Top + work.Height / 2.0;
        Run(Far, 2);
        double hoverY = crawlY;
        Run(new Point((int)crawlX, (int)crawlY), 10);
        Check("hover on the wall pauses crawl", hover && crawlY == hoverY);
        Point grabAt = new Point((int)crawlX, (int)crawlY);
        mouseDown = true; downPt = lastCursor = grabAt; grab = new Point(grabAt.X - (int)x, grabAt.Y - CharTop());
        Run(grabAt, 3, i => new Point(grabAt.X - 30 - i * 10, grabAt.Y));
        Check("can be dragged off the wall", st == St.Drag);
        mouseDown = false; Go(St.Fall, 1e9);
        t = 0;
        while (st == St.Fall && t++ < 200) Run(Far, 1);
        Check("falls back to the floor", air == 0 && x <= work.Right - charW);

        // Crawling and folder perching from the ceiling: falling off lands fine.
        Reset();
        Go(St.Crawl, 1e9);
        crawlEdge = 2; crawlX = work.Left + 500; crawlY = work.Top + charH / 2.0;
        Run(Far, 3);
        Go(St.Coffee, 8);
        t = 0;
        while (st == St.Fall && t++ < 200) Run(Far, 1);
        Check("from the ceiling, jumps down for floor action", st == St.Coffee && air == 0);

        // Task notifications.
        string proj;
        Check("project from hook json", TaskNotice.Project("{\"session_id\":\"a\",\"cwd\":\"C:\\\\code\\\\Мой проект\",\"stop_hook_active\":false}", out proj) && proj == "Мой проект");
        Check("trailing slash", TaskNotice.Project("{\"cwd\":\"C:\\\\code\\\\app\\\\\"}", out proj) && proj == "app");
        Check("scratch folders stay unnamed", TaskNotice.Project("{\"cwd\":\"C:\\\\x\\\\scratch-2026-11-03-4c10d0\"}", out proj) && proj == "");
        Check("empty input still notifies", TaskNotice.Project("", out proj) && proj == "");
        Check("garbage input is safe", TaskNotice.Project("{\"cwd\":\"\\\\q bad\"}", out proj));
        Check("re-run hook is skipped", !TaskNotice.Project("{\"stop_hook_active\": true,\"cwd\":\"C:\\\\a\"}", out proj));

        Reset();
        cfg.Notify = true;
        Go(St.Idle, 1e9);
        IntPtr me = Handle;
        var sender = new System.Threading.Thread(() => TaskNotice.Send(me, "Мой проект"));
        sender.Start();
        for (int i = 0; i < 200 && (sender.IsAlive || st != St.Happy); i++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
        sender.Join();
        Check("message reaches the buddy", st == St.Happy && bubble != null);
        log.Add("     line: " + bubble);
        Reset();
        cfg.Notify = false;
        Go(St.Idle, 1e9);
        TaskDone("x");
        Check("toggle off stays silent", st == St.Idle && bubble == null);
        cfg.Notify = true;
        cfg.Quiet = true;
        Go(St.Sleep, 1e9);
        TaskDone("x");
        Check("quiet keeps sleeping", st == St.Sleep && bubble == null);
        cfg.Quiet = false;
        var highUp = new DeskFolder { Name = "up", Box = new Rectangle(work.Left + 300, work.Top + 200, 76, 90) };
        Reset();
        PerchOn(highUp);
        bubble = null;
        TaskDone("x");
        Check("on a folder he stays there and says it", st == St.Perch && bubble != null);

        // Spider thread from the floor: up, pause, down, hang, then floor or back to the ceiling.
        Func<St> runDangle = () =>
        {
            int ticks = 0;
            while (st == St.Dangle && ticks++ < 3000) Run(Far, 1);
            return st;
        };
        double top = work.Height - charH;
        var ends = new HashSet<string>();
        for (int round = 0; round < 8; round++)
        {
            Reset();
            StartDangle();
            if (round == 0)
            {
                Check("thread starts by reeling up", st == St.Dangle && dangleMode == 0);
                Run(Far, 2);
                Check("window grows to full height", tall && winH == bounds.Height);
            }
            int ticks = 0;
            while (st == St.Dangle && dangleMode < 2 && ticks++ < 400) Run(Far, 1);
            if (round == 0) Check("reaches the ceiling", Math.Abs(air - top) < 12 * d + 1);
            while (st == St.Dangle && dangleMode == 2 && ticks++ < 1500) Run(Far, 1);
            if (round == 0)
            {
                Check("stops partway down", st == St.Dangle && dangleMode == 3 && air > 0 && air < top);
                Run(Far, 3);
                Snap("dangle");
            }
            runDangle();
            ends.Add(st == St.Crawl ? "crawl" : air == 0 ? "floor" : "air:" + st);
        }
        Check("ends: " + string.Join(",", ends), ends.Contains("floor") && ends.Contains("crawl") && ends.Count == 2);
        Run(Far, 2);
        Check("window shrinks after the thread", !tall || st == St.Dangle);

        // Starting from the ceiling goes straight down; hover holds it; swinging; tearing it.
        Reset();
        face = 1;
        Go(St.Crawl, 1e9);
        crawlEdge = 2; crawlX = work.Left + work.Width / 2; crawlY = work.Top + charH / 2.0;
        Run(Far, 2);
        StartDangle();
        Check("from the ceiling skips the reel-up", st == St.Dangle && dangleMode == 1);
        int waitTicks = 0;
        while (dangleMode == 1 && waitTicks++ < 40) Run(Far, 1);
        Run(Far, 20);
        double airBefore = air;
        Point onHim = OnBuddy;
        Run(onHim, 10);
        Check("hover holds the descent", air == airBefore);
        Run(onHim, 10, i => new Point(onHim.X + (i % 2 == 0 ? -12 : 12), onHim.Y));
        Check("moving the cursor swings him", swingAmp > 3 * d + 1);
        double xBefore = x;
        Run(onHim, 1, i => new Point(onHim.X + 12, onHim.Y));
        Check("no sideways jump from a flick", Math.Abs(x - xBefore) < 12 * d);
        Check("swing stays near the anchor", Math.Abs(x + charW / 2.0 - dangleAnchor) <= 60 * d + 1);
        double swingBefore = swingGoal;
        HandleClick();
        Check("click pushes him, keeps him hanging", st == St.Dangle && swingGoal > swingBefore);
        Point held = OnBuddy;
        mouseDown = true; downPt = lastCursor = held; grab = new Point(held.X - (int)x, held.Y - CharTop());
        Run(held, 3, i => new Point(held.X + 30 + i * 8, held.Y + i * 6));
        Check("dragging tears the thread", st == St.Drag && bubble == "Паутинка порвалась!");
        mouseDown = false; Go(St.Fall, 1e9);
        waitTicks = 0;
        while (st == St.Fall && waitTicks++ < 200) Run(Far, 1);
        Check("then falls to the floor", air == 0);
        Reset();
        StartDangle();
        Go(St.Coffee, 8);
        Check("floor action drops him off the thread", st == St.Fall && queued == St.Coffee);
        Reset();
        folders.Add(folder);
        PerchOn(folder);
        StartDangle();
        Check("from a folder: jumps down first", st == St.Fall && queued == St.Dangle);
        waitTicks = 0;
        while (st == St.Fall && waitTicks++ < 200) Run(Far, 1);
        Check("then goes up the thread", st == St.Dangle && dangleMode == 0);

        // Riding a real window, kept off-screen so nothing appears.
        using (var rideForm = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-5000, 400), Size = new Size(400, 300), Text = "ride test" })
        {
            rideForm.Show();
            Application.DoEvents();
            Reset();
            var rideTarget = new DeskFolder { Name = "окно", Box = rideForm.Bounds, Hwnd = rideForm.Handle };
            x = work.Left + 100;
            LeapTo(rideTarget);
            int rideTicks = 0;
            while (st == St.Leap && rideTicks++ < 60) Run(Far, 1);
            Check("lands on the window", st == St.Perch && perch == rideTarget);
            Check("feet on its top edge", CharTop() + charH == 400);
            rideForm.Location = new Point(-5000, 500);
            Application.DoEvents();
            Run(Far, 2);
            Check("follows when the window moves", CharTop() + charH == 500 && st == St.Perch);
            rideForm.Location = new Point(-5000, 0);
            Application.DoEvents();
            Run(Far, 2);
            Check("no room above: jumps off", st == St.Fall);
            Reset();
            rideForm.Location = new Point(-5000, 400);
            Application.DoEvents();
            PerchOn(new DeskFolder { Name = "окно", Box = rideForm.Bounds, Hwnd = rideForm.Handle });
            Run(Far, 2);
            rideForm.Hide();
            Run(Far, 2);
            Check("window gone: jumps off", st == St.Fall);
        }
        Reset();
        DeskFolder realTop = Desktop.TopWindow(Handle, work.Top + charH + 4);
        log.Add("     real top window usable: " + (realTop != null));

        // Hide and seek.
        Reset();
        x = work.Left + 200;
        StartPeek();
        Check("runs to the near edge", st == St.Peek && peekSide == -1);
        var seenPhases = new HashSet<int>();
        bool peeked = false, leftShown = false, rightShown = false;
        for (int i = 0; i < 700 && st == St.Peek; i++)
        {
            Run(Far, 1);
            seenPhases.Add(peekPhase);
            if (peekPhase == 3)
            {
                peeked = true;
                if (peekSide < 0) leftShown = true; else rightShown = true;
                Check3(x + charW > work.Left && x < work.Right);
                if (i % 40 == 0) Snap("peek");
            }
        }
        Check("goes through all peek phases", seenPhases.Count == 5);
        Check("partly on screen when peeking", peeked && peekOk);
        Check("timeout comes back on screen", st != St.Peek && x >= work.Left && x <= work.Right - charW);
        log.Add("     peeked left " + leftShown + ", right " + rightShown);
        Reset();
        StartPeek();
        int peekTicks = 0;
        while (peekPhase != 3 && peekTicks++ < 500) Run(Far, 1);
        Snap("peek");
        HandleClick();
        Check("click finds him", st == St.Happy && x >= work.Left && x <= work.Right - charW);
        Reset();
        folders.Add(folder);
        PerchOn(folder);
        StartPeek();
        Check("from a folder: jumps down, then hides", st == St.Fall && queued == St.Peek);

        // The web, frame by frame on a dark and a light background.
        foreach (double stage in new[] { 0.12, 0.3, 0.6, 1.0 })
        {
            var sample = new Web(stage < 0.5 ? -1 : 1, 260 * d) { P = stage };
            int side = (int)sample.Radius + 10;
            using (var b = new Bitmap(side * 2, side))
            using (var bg = Graphics.FromImage(b))
            {
                bg.Clear(Color.FromArgb(40, 60, 85));
                bg.FillRectangle(Brushes.White, side, 0, side, side);
                bg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                sample.Draw(bg, side, 1f, d);
                bg.TranslateTransform(side, 0);
                sample.Draw(bg, side, 1f, d);
                bg.ResetTransform();
                b.Save(Path.Combine(outDir, "web_" + (int)(stage * 100) + ".png"), ImageFormat.Png);
            }
        }

        // The tip moves along the thread with no big jumps, apart from going back to the corner between spokes.
        var path2 = new Web(1, 260 * d) { P = 0.3 };
        double biggest = 0;
        PointF lastTip = path2.Tip();
        for (int i = 1; i <= 2000; i++)
        {
            path2.P = 0.3 + 0.7 * i / 2000.0;
            PointF tp = path2.Tip();
            biggest = Math.Max(biggest, Math.Sqrt(Math.Pow(tp.X - lastTip.X, 2) + Math.Pow(tp.Y - lastTip.Y, 2)));
            lastTip = tp;
        }
        log.Add("     biggest tip jump on the spiral: " + (int)biggest + " px");
        Check("spiral is continuous", biggest < 60 * d);
        Check("tip stays inside the web", lastTip.X >= 0 && lastTip.Y >= 0 && lastTip.X <= 270 * d && lastTip.Y <= 270 * d);

        // Going away: climbs to the corner and weaves, slowly.
        cfg.Weave = true;
        Reset();
        CheckAway(299);
        Check("not yet at 299 s", st == St.Idle && !away && web == null);
        CheckAway(300);
        Check("away starts weaving", st == St.Weave && away && web != null && !webFast);
        int weaveTicks = 0;
        while (!webReached && weaveTicks++ < 200) Run(Far, 1);
        Check("reaches the corner", webReached);
        double cornerY = CharTop();
        Check("up in the top of the screen", cornerY < work.Top + 300 * d);
        double p0 = web.P;
        for (int i = 0; i < 300; i++) Run(Far, 1);
        Check("web grows while away", web.P > p0 && st == St.Weave);
        double expected = p0 + 300 * 0.066 / 600;
        Check("full web takes ten minutes", Math.Abs(web.P - expected) < 0.001);
        Snap("weaving");
        HandleClick();
        Check("a poke does not stop the weaving", st == St.Weave);
        web.P = 0.5;
        CheckAway(1);
        Check("return: not away", !away);
        Check("return: boasts", bubble == Lines.WebBack(0.5));
        Check("return: jumps down, web stays", st == St.Fall && queued == St.Happy && web != null);
        weaveTicks = 0;
        while (st == St.Fall && weaveTicks++ < 300) Run(Far, 1);
        Check("lands softly and is happy", st == St.Happy && web != null);
        Check("lines depend on size", Lines.WebBack(0.05) != Lines.WebBack(0.5) && Lines.WebBack(0.5) != Lines.WebBack(0.9));

        // The web fades two minutes after the return.
        Go(St.Idle, 1e9);
        webHold = 0.2;
        for (int i = 0; i < 10; i++) Run(Far, 1);
        Check("starts to fade", web != null && webAlpha < 1);
        for (int i = 0; i < 70 && web != null; i++) Run(Far, 1);
        Check("then is gone", web == null);

        // Finished while away: sleeps on it, and leaves it alone.
        Reset();
        CheckAway(400);
        web.P = 0.9995;
        for (int i = 0; i < 40; i++) Run(Far, 1);
        Check("finished web: stays on it", st == St.Weave && web.P >= 1);
        Check("finished web: keeps it fresh", webAlpha == 1);
        CheckAway(1);
        Check("big web gets the big line", bubble == Lines.WebBack(1));

        // On request: half a minute, then down.
        Reset();
        StartWeave();
        Check("menu weave is fast", st == St.Weave && webFast);
        weaveTicks = 0;
        while (st == St.Weave && weaveTicks++ < 600) Run(Far, 1);
        Check("menu weave takes about 30 s (" + (int)(weaveTicks * 0.066) + ")", web != null && web.P >= 1 && Math.Abs(weaveTicks * 0.066 - 30) < 4);
        Check("menu weave ends with a jump", st == St.Fall);
        ClearWeb();
        Check("clear removes it", web == null);

        // What must not start it.
        Reset();
        cfg.Quiet = true;
        CheckAway(400);
        Check("quiet mode does not weave", st == St.Idle && web == null);
        cfg.Quiet = false;
        Reset();
        cfg.Weave = false;
        Go(St.Walk, 40);
        CheckAway(400);
        Check("toggle off: sleeps as before", st == St.Sleep && web == null);
        cfg.Weave = true;
        Reset();
        musicOn = true;
        CheckAway(400);
        Check("music keeps him dancing instead", st != St.Weave && web == null);
        musicOn = false;

        // From a folder, from random sleep, and grabbed mid-weave.
        Reset();
        PerchOn(folder);
        CheckAway(400);
        Check("from a folder he goes straight there", st == St.Weave);
        Reset();
        Go(St.Sleep, 1e9);
        CheckAway(400);
        Check("wakes from a nap to weave", st == St.Weave);
        Run(Far, 5);
        Point weaver = OnBuddy;
        mouseDown = true; downPt = lastCursor = weaver; grab = new Point(weaver.X - (int)x, weaver.Y - CharTop());
        Run(weaver, 3, i => new Point(weaver.X + 30 + i * 8, weaver.Y + i * 6));
        Check("can be grabbed mid-weave", st == St.Drag && web != null);
        mouseDown = false; Go(St.Fall, 1e9);
        weaveTicks = 0;
        while (st == St.Fall && weaveTicks++ < 300) Run(Far, 1);
        Check("dropped, lands on the floor", air == 0);
        CheckAway(1);
        Check("return after that is calm", !away);

        // Every state renders at every size without throwing.
        foreach (int size in new[] { 0, 1, 2 })
        {
            cfg.Size = size;
            ApplySize();
            foreach (St s in Enum.GetValues(typeof(St)))
            {
                Reset();
                if (s == St.Play) StartToy(true); else Go(s, 1e9);
                Run(Far, 12);
            }
        }
        Check("all states render at all sizes", true);

        File.WriteAllLines(Path.Combine(outDir, "result.txt"), log);
        return failed;
    }
}

static class BuddyTest
{
    [STAThread]
    static int Main(string[] args)
    {
        Native.SetProcessDPIAware();
        Directory.CreateDirectory(args[0]);
        try
        {
            using (var f = new BuddyForm())
            {
                int failed = f.SelfTest(args[0]);
                Painter.Sheet(Path.Combine(args[0], "sheet.png"));
                return failed;
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(args[0], "crash.txt"), ex.ToString());
            return 99;
        }
    }
}
