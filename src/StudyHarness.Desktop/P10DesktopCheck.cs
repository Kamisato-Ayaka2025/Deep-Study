using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P10DesktopCheck
{
    private const string Code = "// 中文注释 <script>window.injected=1</script>\r\n\tint count = 42;\r\nconst char* text = \"hello <world>\";\r\nfor (int i = 0; i < count; ++i) { /* 展示，不执行 */ }";
    private static TheoryLesson Content(int count, int schema) => new(
        [new("title", "heading", "P10 · 代码、数学与测验"),
         new("intro", "paragraph", "代码只用于阅读。短式 $x_1^2$ 使用上下标，裸 x_1 保持原文。"),
         new("code", "code", Code),
         new("fence", "example", "```cpp\r\n" + Code + "\r\n```\r\n依据分配律，分别展开下列两步："),
         new("steps", "paragraph", @"$$A(u+v)=Au+Av$$" + "\n" + @"\[A(ku)=kAu\]"),
         new("matrix", "math", @"\begin{pmatrix}1&2\\3&4\end{pmatrix}\begin{pmatrix}x_1\\x_2\end{pmatrix}"),
         new("wide", "math", string.Join("+", Enumerable.Repeat(@"x_{123456789}", 24))),
         new("visual", "diagram", """{"type":"flow","title":"传输示意","nodes":["发送","接收"],"edges":[[0,1,"数据"]]}"""),
         new("question", "question", QuestionId: "q1")],
        [new("q1", "```c\nint n = 2;\n```\n上面示例中 n 的值是多少？",
            Enumerable.Range(0, count).Select(i => new LessonOption("id" + i, (i + 2).ToString())).ToArray(),
            "id0", "原选项A正确：n被赋值为2。矩阵 A 与变量 a 不应机械替换。")], ["s1"], schema);

    public static async Task RunAsync(string root, string evidence)
    {
        Directory.CreateDirectory(evidence);
        var checks = new List<string>(); LearningWindow? window = null;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            if (!File.Exists(Path.Combine(root, "p10-synthetic.marker"))) throw new InvalidDataException("Synthetic marker required");
            var plans = new SqlitePlanningStore(root); var store = new SqliteLearningStore(root, plans);
            var draft = (await plans.ListAsync()).SingleOrDefault(); var restart = draft is not null;
            var nodes = new[] { ("n2", 2, 1), ("n3", 3, 2), ("n4", 4, 2), ("n6", 6, 1), ("new", 4, 3) };
            if (draft is null)
            {
                var sources = new[] { new PlanSource("s1", new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 64),
                    new("p10-synthetic.pdf", 1, "第1页（OCR，待复核）", "1")), false) };
                var input = new PlanningInput("P10合成显示", "基础", "代码与数学", 120, "Math", []);
                draft = (await plans.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
                var candidate = new PlanCandidate("P10合成显示", "基础", "代码与数学", 120, "Math", [], [], [], ["s1"],
                    nodes.Select(n => new PlannedNode(n.Item1, n.Item1, "theory", ["代码与数学"], [], 20, null)).ToArray());
                var plan = PlanRules.Validate(draft.Id, input, candidate, sources, null);
                Check(plan.Ok, "synthetic-plan-valid");
                draft = (await plans.SaveProposalAsync(new(Guid.NewGuid(), 0, plan.Value!), draft.Id)).Value!;
                draft = (await plans.ConfirmAsync(new(Guid.NewGuid(), draft.Revision, new(draft.Id, plan.Value!.Hash, true)))).Value!;
                foreach (var (key, count, schema) in nodes)
                    Check((await store.PublishAsync(new(Guid.NewGuid(), 0, Content(count, schema)), draft.Id, key, draft.Plan!.Hash, sources)).Ok, "publish-" + key);
            }
            var provider = new TheoryFixtureProvider();
            var generation = new GenerationService(plans, store, store, new KnowledgeStore(root),
                new(new SqliteSettingsStore(root)), new(provider), new TheoryLessonParser());
            window = new(generation, draft.Id, "n2", root); window.Show();
            await window.ReaderInitialized.Task.WaitAsync(TimeSpan.FromSeconds(30));
            async Task<bool> Js(string expression) => await window.Browser.ExecuteScriptAsync(expression) == "true";
            async Task Wait(string expression)
            {
                for (var i = 0; i < 100; i++) { if (await Js(expression)) return; await Task.Delay(100); }
                throw new InvalidOperationException("Reader wait timed out: " + expression);
            }
            async Task Capture(string name, string target)
            {
                await window.Browser.ExecuteScriptAsync($"document.getElementById('{target}').scrollIntoView()");
                await Task.Delay(250);
                await using var image = File.Create(Path.Combine(evidence, (restart ? "restart-" : "") + name + ".png"));
                await window.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            await Wait("!!document.querySelector('#code pre')");
            Check(await Js("document.getElementById('sources').textContent.includes('OCR未复核')"), "ocr-status-in-source-information");
            Check(await Js("!document.getElementById('content').textContent.includes('OCR未复核')"), "ocr-status-not-in-lesson-body");
            Check(await Js("!!document.querySelector('#code .token-keyword') && !!document.querySelector('#code .token-comment') && !!document.querySelector('#code .token-string') && !!document.querySelector('#code .token-number')"), "code-four-token-categories");
            Check(await Js("new Set(['keyword','comment','string','number'].map(k=>getComputedStyle(document.querySelector('#code .token-'+k)).color)).size===4"), "four-distinct-computed-colors");
            Check(await Js("document.querySelector('#code pre').textContent===" + JsonSerializer.Serialize(Code)), "code-exact-crlf-tab-unicode");
            Check(await Js("document.querySelector('#fence pre').textContent===" + JsonSerializer.Serialize(Code)), "fence-exact-crlf-tab-unicode");
            Check(await Js("!!document.querySelector('.question-prompt .token-keyword')"), "question-fence-colored");
            await Capture("optional-diagram", "visual");
            Check(await Js("document.querySelectorAll('#visual svg rect').length===2 && document.querySelector('#visual').textContent.includes('传输示意')"), "optional-flow-lesson-renders");
            Check(await Js("lessonDiagram(JSON.stringify({type:'bar',labels:['A','B','C'],values:[-2,0,5]})).querySelectorAll('rect').length===3"), "optional-bar-negative-zero-values");
            Check(await Js("!!lessonDiagram(JSON.stringify({type:'line',labels:['A','B'],values:[1,2]})).querySelector('polyline')"), "optional-line-renders");
            Check(await Js("lessonDiagram('{bad json').querySelector('pre').textContent==='{bad json'"), "malformed-diagram-falls-back-to-text");
            Check(await Js("(()=>{const f=lessonDiagram(JSON.stringify({type:'flow',nodes:['<img src=x onerror=alert(1)>'],edges:[]}));return !f.querySelector('img,script')&&f.textContent.includes('<img');})()"), "diagram-label-html-is-inert");
            Check(await Js("lessonDiagram('A -> B -> C').querySelectorAll('rect').length===3"), "legacy-arrow-diagram-supported");
            Check(await Js("!window.injected && !document.querySelector('#content script,#content img')"), "literal-html-is-inert");
            Check(await Js("""
                (()=>{const s='<svg onload=alert(1)> 中文';const p=renderCode(s,'unknown');return p.textContent===s&&!p.children.length;})()
                """), "unknown-language-plain-fallback");
            Check(await Js("""
                (()=>{const s='int '.repeat(17000);const p=renderCode(s,'cpp');return p.textContent===s&&!p.children.length;})()
                """), "oversize-complete-fallback");
            Check(await Js("""
                (()=>{const s='x '.repeat(5000);const p=renderCode(s,'cpp');return p.textContent===s&&!p.children.length;})()
                """), "token-budget-complete-fallback");
            Check(await Js("""
                (()=>{const s='"unterminated\\中文';const p=renderCode(s);return p.textContent===s;})()
                """), "unterminated-string-keeps-text");
            Check(await Js("""
                (()=>{const p=renderCode('/* unfinished 中文');return p.textContent==='/* unfinished 中文';})()
                """), "unterminated-comment-keeps-text");
            Check(await Js("""
                (()=>{const p=paragraph('```cpp\r\nint x = 1;\r\n');return p.querySelector('pre').textContent==='int x = 1;\r\n';})()
                """), "unclosed-fence-keeps-final-newline");
            window.Activate();
            window.Browser.Focus();
            await Task.Delay(150);
            Check(await Js("""
                (()=>{const r=document.createRange();r.selectNodeContents(document.querySelector('#code pre'));const s=getSelection();s.removeAllRanges();s.addRange(r);return s.toString()===document.querySelector('#code pre').textContent;})()
                """), "selection-copy-text-exact");
            // Exercise the actual browser copy command, restoring the user's clipboard in memory.
            var clipboard = Clipboard.GetDataObject();
            try
            {
                Check(await Js("document.execCommand('copy')"), "browser-copy-command");
                // WebView2 publishes to the system clipboard asynchronously. Keep the
                // exact content assertion, but allow the native message queue to drain.
                for (var attempt = 0; attempt < 50 && Clipboard.GetText().Replace("\r\n", "\n") != Code.Replace("\r\n", "\n"); attempt++)
                    await Task.Delay(40);
                Check(Clipboard.GetText().Replace("\r\n", "\n") == Code.Replace("\r\n", "\n"), "clipboard-copy-preserves-code");
            }
            finally { if (clipboard is not null) Clipboard.SetDataObject(clipboard); else Clipboard.Clear(); }
            await window.Browser.ExecuteScriptAsync("getSelection().removeAllRanges()");
            Check(await Js("!!document.querySelector('#intro .katex') && document.querySelector('#intro').textContent.includes('裸 x_1')"), "marked-subscripts-and-unmarked-text");
            Check(await Js("document.querySelectorAll('#steps .display-math .katex').length===2 && [...document.querySelectorAll('#steps .display-math')].every(e=>getComputedStyle(e).display==='block')"), "two-display-delimiters-independent-lines");
            Check(await Js("!!document.querySelector('#matrix .katex .mtable')"), "matrix-rendered");
            Check(await Js("document.querySelector('#wide .katex-display').scrollWidth>document.querySelector('#wide .katex-display').clientWidth && getComputedStyle(document.querySelector('#wide .katex-display')).overflowX==='auto'"), "wide-math-local-scroll");
            Check(await Js("""
                (()=>{const e=document.createElement('div');const s='\\notARealCommand';math(s,e,true);return e.textContent===s&&!e.querySelector('.katex');})()
                """), "invalid-tex-text-fallback");
            Check(await Js("""
                (()=>{const e=document.createElement('div');math('\\href{javascript:alert(1)}{bad}',e);return !e.querySelector('a');})()
                """), "katex-untrusted-link-rejected");
            Check(await Js("""
                (()=>{const e=document.createElement('div');e.id='chinese-math';mathBlock('[X_T]_{补}=2^n+X_T \\pmod{2^n},\\quad -2^{n-1}\\le X_T<2^{n-1}',e);document.getElementById('content').append(e);return !!e.querySelector('.katex')&&e.textContent.includes('补');})()
                """), "chinese-subscript-math-rendered");
            Check(await Js("""
                (()=>{const e=paragraph('选择 Y 或 \\overline{Y}，再计算 \\frac{1}{2}。');return e.querySelectorAll('.katex').length===2&&e.textContent.includes('选择 Y 或');})()
                """), "unmarked-explicit-tex-in-prose");
            Check(await Js("""
                (()=>{const e=paragraph('`\\overline{Y}` 与裸 x_1\n```cpp\n\\frac{1}{2}\n```');return !e.querySelector('.katex')&&e.querySelector('code').textContent==='\\overline{Y}'&&e.querySelector('pre').textContent==='\\frac{1}{2}';})()
                """), "unmarked-tex-preserves-code-and-identifiers");
            Check(await Js("""
                (()=>{const e=paragraph('1.5=1.1_2×2^0，0.25=1.0_2×2^{-2}。0.01_2+1.1_2=1.11_2；01111111_2，1100…0_2。');e.id='numeric-math';document.getElementById('content').append(e);return e.querySelectorAll('.katex').length===9&&[...e.querySelectorAll('.inline-math')].every(s=>getComputedStyle(s).display==='inline'&&getComputedStyle(s).overflowX==='visible');})()
                """), "unmarked-numeric-scripts");
            Check(await Js("""
                (()=>{const e=paragraph('[E_b]移=[E_x]移+[-[E_y]移]补+127 (mod 2^8)。');document.getElementById('numeric-math').append(e);return e.querySelectorAll('.katex').length===4&&e.textContent.includes('移')&&e.textContent.includes('补');})()
                """), "unmarked-bracketed-exponents");
            Check(await Js("""
                (()=>{const s='file_2.txt x_1 C:\\data\\2^8 /tmp/1.1_2.txt `1.1_2×2^0`';const e=paragraph(s);return !e.querySelector('.katex')&&e.textContent.includes('file_2.txt')&&e.querySelector('code').textContent==='1.1_2×2^0';})()
                """), "numeric-recovery-keeps-paths-identifiers-code");
            Check(await Js("""
                (()=>{const e=paragraph('索引位数=\\log_2(行数)；组相联索引位数=\\log_{2}(组数)；块内地址位数=\\log_2(块大小)。另有 \\ln(x+1)、\\log_2((x+1)/2)。');e.id='log-math';document.getElementById('content').append(e);return e.querySelectorAll('.katex').length===5&&e.textContent.includes('行数')&&e.textContent.includes('块大小');})()
                """), "unmarked-logarithms-with-chinese-arguments");
            Check(await Js("""
                (()=>{const e=paragraph('`\\log_2(行数)` 与 \\unknown_2(x) 和 C:\\log_2(file)');return !e.querySelector('.katex')&&e.querySelector('code').textContent==='\\log_2(行数)'&&e.textContent.includes('\\unknown_2(x)');})()
                """), "function-recovery-preserves-code-paths-unknown-commands");
            await Capture("log-math", "log-math");
            await Capture("numeric-math", "numeric-math");
            await Capture("chinese-math", "chinese-math");
            await Capture("code", "code"); await Capture("math", "steps");
            var reuseReport = await window.Browser.ExecuteScriptAsync("""
                (()=>{
                  const original=current, sample=structuredClone(current);
                  sample.blocks=[...Array.from({length:240},(_,i)=>({id:'perf-'+i,kind:'paragraph',text:'长课程公式 $x_1^2 + x_2^2$ 与代码 `value`。'.repeat(30)})),...sample.blocks];
                  sample.canAnswer=false;
                  let start=performance.now();render(sample);document.body.offsetHeight;const initialMs=performance.now()-start;
                  const nodes=[...document.querySelectorAll('#content > section')];
                  start=performance.now();render(structuredClone(sample));document.body.offsetHeight;const refreshMs=performance.now()-start;
                  const refreshed=[...document.querySelectorAll('#content > section')];const reused=nodes.filter(n=>refreshed.includes(n)).length;
                  const updated=structuredClone(sample);updated.blocks[0].text='更新正文';render(updated);
                  const changed=document.getElementById('perf-0')!==nodes[0]&&document.getElementById('perf-1')===nodes[1];
                  updated.canAnswer=true;render(updated);const enabled=[...document.querySelectorAll('input')].every(n=>!n.disabled);
                  render(original);
                  return {initialMs,refreshMs,reused,count:nodes.length,changed,enabled};
                })()
                """);
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "p28-reader-restart.json" : "p28-reader.json"), reuseReport);
            using (var reuse = JsonDocument.Parse(reuseReport))
            {
                var result = reuse.RootElement;
                if (result.GetProperty("reused").GetInt32() != result.GetProperty("count").GetInt32() ||
                    !result.GetProperty("changed").GetBoolean() || !result.GetProperty("enabled").GetBoolean())
                    throw new InvalidOperationException("P28 reader reuse or permission invalidation failed");
            }

            var orders = new Dictionary<string, string>(); var hashes = new Dictionary<string, string>();
            var expected = restart ? JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Path.Combine(evidence, "orders.json")))! : null;
            foreach (var (key, count, _) in nodes)
            {
                window.Model.Selected = window.Model.Nodes.Single(n => n.Key == key); await window.Model.OpenAsync();
                await Wait($"current?.identity.nodeKey==='{key}'");
                var lesson = (await store.GetLessonAsync(draft.Id, key)).Value!;
                orders[key] = string.Join(",", lesson.Questions.Single().Options.Select(o => o.Id)); hashes[key] = lesson.Identity.LessonHash;
                Check(await Js($"document.querySelectorAll('.question input').length==={count}"), "original-count-" + key);
                Check(await Js("current.questions[0].options.every((o,i)=>o.displayLabel===String.fromCharCode(65+i))"), "labels-" + key);
                if (restart) Check(orders[key] == expected![key], "restart-stable-" + key);
                if (!restart)
                {
                    Check(await Js("!current.questions[0].answer && !document.querySelector('.answer')"), "hidden-answer-" + key);
                    await window.Browser.ExecuteScriptAsync("document.querySelector('input[value=id1]').click();document.querySelector('form').requestSubmit()");
                    await Wait("document.getElementById('state') && !current.questions[0].everCorrect");
                    for (var i = 0; i < 100 && (await store.AttemptsAsync(draft.Id, key)).Count == 0; i++) await Task.Delay(100);
                    Check((await store.AttemptsAsync(draft.Id, key)).Single().OptionId == "id1", "wrong-original-id-" + key);
                    await window.Model.OpenAsync(); await Wait($"current?.identity.nodeKey==='{key}' && !current.questions[0].answer");
                    Check(string.Join(",", (await store.GetLessonAsync(draft.Id, key)).Value!.Questions[0].Options.Select(o => o.Id)) == orders[key], "wrong-retry-stable-" + key);
                    await window.Browser.ExecuteScriptAsync("document.querySelector('input[value=id0]').click();document.querySelector('form').requestSubmit()");
                    await Wait("!!current.questions[0].answer");
                }
                Check(await Js("current.questions[0].answer.correctDisplayLabel===current.questions[0].options.find(o=>o.id==='id0').displayLabel"), "correct-display-label-" + key);
                Check(await Js("document.querySelector('.original-labels') === null && !document.querySelector('.answer').textContent.includes('解析中的编号对应原始顺序') && document.querySelector('.answer').textContent.includes('矩阵 A 与变量 a')"), "legacy-explanation-without-mapping-" + key);
                await window.Model.RefreshAsync(); await window.Model.OpenAsync();
                Check(string.Join(",", (await store.GetLessonAsync(draft.Id, key)).Value!.Questions[0].Options.Select(o => o.Id)) == orders[key], "refresh-stable-" + key);
                Check((await store.GetLessonAsync(draft.Id, key)).Value!.Identity.LessonHash == hashes[key], "immutable-hash-" + key);
            }
            if (!restart)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, "orders.json"), JsonSerializer.Serialize(orders));
                await File.WriteAllTextAsync(Path.Combine(evidence, "hashes.json"), JsonSerializer.Serialize(hashes));
            }
            else Check(JsonSerializer.Serialize(hashes) == await File.ReadAllTextAsync(Path.Combine(evidence, "hashes.json")), "restart-all-hashes");
            await Capture("quiz", "question");
            Check(provider.Calls == 0, "reading-restart-zero-model-calls");
            Check((await store.CompletedNodesAsync(draft.Id)).Count == 5, "mastery-persisted");
            await Task.Delay(500);
            await window.Browser.ExecuteScriptAsync("document.querySelectorAll('header,footer,section:not(#visual)').forEach(e=>e.style.display='none');window.scrollTo(0,0)");
            await Capture("diagram-feature", "visual");
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new
                { checks, failed = 0, manualPending = true, codeExecuted = false, syntheticProgressOnly = true }, Wire.Options));
            window.Close(); System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, error = e.ToString() }, Wire.Options));
            window?.Close(); System.Windows.Application.Current.Shutdown(1);
        }
    }
}
