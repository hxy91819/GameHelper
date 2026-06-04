# 🚀 快速参考：避免模糊匹配类缺陷

> 打印此页面，贴在显示器旁边！

---

## ⚡ 5 秒规则

**在写任何涉及匹配/算法的代码前，问自己：**

1. ❓ 最短的输入是什么？（如 2 字符）
2. ❓ 什么不应该被匹配？（如系统工具）
3. ❓ 如何验证上下文？（如路径）
4. ❓ 阈值合理吗？（如 80 分对短字符串太低）
5. ❓ 有负向测试吗？（如测试应拒绝的场景）

**如果任何一个答案是"不知道"，停下来，做边界案例分析！**

---

## 🎯 黄金法则

### 法则 1：永远不要单独依赖算法
```
❌ if (similarity > 80) return match;
✅ if (similarity > threshold && pathOK && !blacklist) return match;
```

### 法则 2：短字符串需要特殊处理
```
2-4 字符 → 阈值 95+
5-8 字符 → 阈值 90+
9+ 字符 → 阈值 80+
```

### 法则 3：至少 30% 的测试是负向测试
```
✅ ShouldMatch_LegitimateCase()
✅ ShouldNotMatch_SystemTool()      ← 更重要！
✅ ShouldNotMatch_DifferentPath()   ← 更重要！
```

### 法则 4：使用真实数据测试
```
❌ Test("game1.exe", "game2.exe")
✅ Test("RE.exe", "reg.exe")  // 真实的系统工具
```

---

## 📋 需求阶段检查清单（2 分钟）

在写验收标准前：

- [ ] 列出 5 个极端输入（最小、最大、空、特殊字符、边界）
- [ ] 列出 5 个应该拒绝的场景（系统工具、错误路径等）
- [ ] 列出 3 个干扰因素（系统、用户、数据）
- [ ] 确定阈值并说明理由
- [ ] 设计多层验证策略

**如果任何一项不清楚，不要开始实现！**

---

## 🧪 测试阶段检查清单（5 分钟）

在提交代码前：

- [ ] 有测试覆盖最短输入（如 2 字符）
- [ ] 有测试覆盖最长输入（如 1000 字符）
- [ ] 有测试覆盖阈值边界（threshold-1, threshold, threshold+1）
- [ ] 有测试覆盖至少 5 个应拒绝的场景
- [ ] 有测试使用真实的系统工具名称
- [ ] 负向测试 ≥ 30%

**如果任何一项没有，不要提交！**

---

## 🔍 代码评审检查清单（3 分钟）

评审涉及算法的代码时：

- [ ] 阈值是否合理？是否需要动态调整？
- [ ] 是否有多层验证？（不只依赖算法）
- [ ] 是否有黑名单/白名单？
- [ ] 是否有详细的日志记录？
- [ ] 是否有负向测试？
- [ ] 是否测试了真实数据？
- [ ] 是否考虑了性能影响？

**如果任何一项是"否"，要求修改！**

---

## 🚨 危险信号

看到这些，立即警惕：

- 🚩 固定阈值（如 `> 80`）用于所有场景
- 🚩 没有路径/上下文验证
- 🚩 没有黑名单机制
- 🚩 只有正向测试，没有负向测试
- 🚩 只用合成数据测试
- 🚩 验收标准模糊（如"相似即可"）
- 🚩 没有边界案例分析

---

## 💡 快速修复模式

发现类似问题时：

### 步骤 1：添加多层验证（5 分钟）
```csharp
// 添加这三层
1. 动态阈值：CalculateThreshold(name)
2. 路径验证：IsPathRelated(path1, path2)
3. 黑名单：!IsSystemPath(path)
```

### 步骤 2：添加负向测试（10 分钟）
```csharp
[Theory]
[InlineData("reg.exe")]  // 系统工具
[InlineData("cmd.exe")]  // 系统工具
public void ShouldNotMatch_SystemTools(string tool) { }
```

### 步骤 3：添加日志（5 分钟）
```csharp
_logger.LogInformation(
    "匹配决策: {Name1} vs {Name2}, Score={Score}, Threshold={Threshold}, Result={Result}",
    name1, name2, score, threshold, result);
```

---

## 📚 相关文档

- **详细复盘**：`docs/retrospective-fuzzy-matching-defect.md`
- **经验教训**：`docs/lessons-learned-summary.md`
- **边界案例模板**：`.bmad-core/templates/boundary-case-analysis-tmpl.md`
- **修复方案**：`docs/stories/1.7.enhance-fuzzy-matching-safety.md`

---

## 🎓 记住这句话

> **"假设最坏的情况会发生，然后设计防御措施。"**

---

**打印日期：** 2025-11-11  
**版本：** 1.0
