namespace QwenTray.Tests;

// S4（2026-09-12）：llama-server stdout 解析。
//
// 这是**零成本拿到真实推理速度的唯一途径**（新版 /slots 已删 timings 字段、/metrics 默认 501），
// 所以解析器一旦坏掉，性能台账会静默失去数据 —— 而且不报错，只是"没有样本"。
// 下面三行都是真实日志原文（来自 E:\llm-deploy\llama.log）。
public class LlamaLogParserTests {
  const string PromptLine =
    "llama_perf_context_print: prompt eval time =    2731.49 ms /  4696 tokens (    0.58 ms per token,  1719.21 tokens per second)";
  const string DecodeLine =
    "llama_perf_context_print:        eval time =    2404.48 ms /   158 tokens (   15.22 ms per token,    65.71 tokens per second)";

  [Fact] public void ParsesPromptEvalLine() {
    Assert.True(LlamaLogParser.TryParse(PromptLine, out var ppTps, out var ppN, out var tgTps, out var tgN));
    Assert.Equal(4696, ppN);
    Assert.Equal(1719.21, ppTps, 2);
    Assert.Equal(0, tgTps);   // 一行只可能是一个方向的统计
    Assert.Equal(0, tgN);
  }

  [Fact] public void ParsesDecodeEvalLine() {
    Assert.True(LlamaLogParser.TryParse(DecodeLine, out var ppTps, out var ppN, out var tgTps, out var tgN));
    Assert.Equal(158, tgN);
    Assert.Equal(65.71, tgTps, 2);
    Assert.Equal(0, ppTps);
    Assert.Equal(0, ppN);
  }

  // "prompt eval time" 必须走 prompt 分支 —— 不能因为该行同时含 "eval time" 被误判成 decode
  [Fact] public void PromptBranchWinsForPromptLines() {
    Assert.True(LlamaLogParser.TryParse(PromptLine, out _, out var ppN, out var tgTps, out _));
    Assert.Equal(4696, ppN);
    Assert.Equal(0, tgTps);
  }

  [Theory]
  [InlineData("llama_perf_context_print:        load time =     987.65 ms")]                 // 无 "/ N tokens"
  [InlineData("llama_perf_context_print:       total time =    5136.00 ms /  4854 tokens")] // 有 tokens 但不是 eval time
  [InlineData("llama_perf_context_print: prompt eval time = 1234.00 ms / 0 tokens")]         // tokens=0 ⇒ 拒绝
  [InlineData("")]
  [InlineData("   ")]
  [InlineData((string?)null)]
  [InlineData("随便一行无关输出")]
  public void RejectsEverythingThatIsNotAnEvalLine(string? line) {
    Assert.False(LlamaLogParser.TryParse(line, out _, out _, out _, out _));
  }

  // 没有 "(... tokens per second)" 括号时，用 ms / tokens 反算吞吐：
  // 1000 ms / 1000 tokens ⇒ 0.5 s ⇒ 1000 t/s
  [Fact] public void DerivesThroughputFromMsWhenNoTpsParenthesis() {
    Assert.True(LlamaLogParser.TryParse("prompt eval time = 1000.00 ms / 1000 tokens",
      out var ppTps, out var ppN, out _, out _));
    Assert.Equal(1000.0, ppTps, 3);
    Assert.Equal(1000, ppN);
  }

  [Fact] public void IsCaseInsensitive() {
    Assert.True(LlamaLogParser.TryParse("PROMPT EVAL TIME = 100.00 ms / 10 tokens (1.00 ms per token, 100.00 tokens per second)",
      out var ppTps, out var ppN, out _, out _));
    Assert.Equal(100.0, ppTps, 3);
    Assert.Equal(10, ppN);
  }

  // 0 ms ⇒ 无法反算吞吐 ⇒ 拒绝（避免除以 0 得到 0 或 Infinity 混进台账）
  [Fact] public void ZeroMs_IsRejected() {
    Assert.False(LlamaLogParser.TryParse("prompt eval time = 0.00 ms / 100 tokens", out _, out _, out _, out _));
  }
}
