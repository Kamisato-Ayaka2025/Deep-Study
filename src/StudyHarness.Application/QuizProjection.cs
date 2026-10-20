using StudyHarness.Contracts;
using StudyHarness.Domain;

namespace StudyHarness.Application;

public static class QuizProjection
{
    public static ReaderQuestion Project(LessonQuestion question, QuizOptionOrder order, bool everCorrect)
    {
        if (!OptionPermutation.IsPermutation(question.Options.Select(o => o.Id).ToArray(), order.OrderedOptionIds))
            throw new InvalidDataException("选项排列记录无效，请恢复学习库备份后重试。");
        var byId = question.Options.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var options = order.OrderedOptionIds.Select((id, i) =>
            new PresentedLessonOption(id, byId[id].Text, OptionPermutation.Label(i))).ToArray();
        var labels = options.ToDictionary(o => o.Id, o => o.DisplayLabel, StringComparer.Ordinal);
        var answer = everCorrect ? new AnswerDisclosure(question.CorrectOptionId, question.Explanation,
            labels[question.CorrectOptionId], question.Options.Select((o, i) =>
                new OptionLabelMapping(OptionPermutation.Label(i), labels[o.Id])).ToArray()) : null;
        return new(question.Id, question.Prompt, options, everCorrect, answer);
    }
}
