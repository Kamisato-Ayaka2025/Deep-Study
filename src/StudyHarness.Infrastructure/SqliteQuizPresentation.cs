using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Domain;

namespace StudyHarness.Infrastructure;

public sealed class CryptoOptionRandomSource : IOptionRandomSource
{
    public int NextIndex(int exclusiveUpperBound) => RandomNumberGenerator.GetInt32(exclusiveUpperBound);
}

public sealed partial class SqliteLearningStore
{
    private QuizOptionOrder OptionOrder(SqliteConnection db, SqliteTransaction tx, LessonIdentity lesson, LessonQuestion question)
    {
        var ids = question.Options.Select(o => o.Id).ToArray();
        var json = Scalar(db, tx,
            "SELECT json FROM quiz_option_orders WHERE subject=$p0 AND node=$p1 AND lesson_hash=$p2 AND question=$p3",
            lesson.SubjectId.ToString(), lesson.NodeKey, lesson.LessonHash, question.Id) as string;
        var order = json is null
            ? new QuizOptionOrder(OptionPermutation.AlgorithmVersion, OptionPermutation.Create(ids, optionRandom.NextIndex))
            : Read<QuizOptionOrder>(json);
        if (order is null || order.AlgorithmVersion != OptionPermutation.AlgorithmVersion ||
            !OptionPermutation.IsPermutation(ids, order.OrderedOptionIds))
            throw new InvalidDataException("选项排列记录无效，请恢复学习库备份后重试。");
        if (json is null)
            Execute(db, tx, "INSERT INTO quiz_option_orders VALUES($p0,$p1,$p2,$p3,$p4)",
                lesson.SubjectId.ToString(), lesson.NodeKey, lesson.LessonHash, question.Id, Json(order));
        return order;
    }

    public Task<Result<QuizOptionOrder>> GetOrCreateOptionOrderAsync(
        LessonIdentity lesson, string questionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (lesson is null || lesson.SubjectId == Guid.Empty || string.IsNullOrWhiteSpace(lesson.NodeKey) ||
            string.IsNullOrWhiteSpace(lesson.LessonHash) || string.IsNullOrWhiteSpace(questionId))
            return Task.FromResult(Bad<QuizOptionOrder>());
        try
        {
            using var db = Open();
            // Acquire the writer lock before reading: concurrent first readers use the winner.
            using var tx = db.BeginTransaction();
            if (Deleted(db, tx, lesson.SubjectId))
                return Task.FromResult(Result<QuizOptionOrder>.Fail(ErrorCode.SUBJECT_DELETING, "学科已删除。"));
            var content = Lesson(db, tx, lesson.SubjectId, lesson.NodeKey);
            if (content?.Hash != lesson.LessonHash)
            {
                var archived = Scalar(db, tx,
                    "SELECT json FROM lesson_history WHERE subject=$p0 AND node=$p1 AND hash=$p2",
                    lesson.SubjectId.ToString(), lesson.NodeKey, lesson.LessonHash) as string;
                content = archived is null ? null : Read<StoredHistory>(archived).Original;
            }
            var question = content?.Hash == lesson.LessonHash
                ? content.Content.Questions.SingleOrDefault(q => q.Id == questionId) : null;
            if (question is null) return Task.FromResult(Bad<QuizOptionOrder>());
            var order = OptionOrder(db, tx, lesson, question);
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return Task.FromResult(Result<QuizOptionOrder>.Success(order));
        }
        catch (Exception e) when (e is SqliteException or JsonException or InvalidDataException)
        {
            return Task.FromResult(Bad<QuizOptionOrder>("无法读取或保存选项排列，请检查学习库或恢复备份后重试。"));
        }
    }
}
