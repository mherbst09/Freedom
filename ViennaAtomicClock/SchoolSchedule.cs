namespace ViennaAtomicClock;

internal sealed record Lesson(int Number, TimeSpan Start, TimeSpan End);

internal enum SchoolPhase { BeforeSchool, Lesson, Break, AfterSchool, Weekend }

/// <summary>What is happening at a given moment and until when.</summary>
internal sealed record SchoolState(SchoolPhase Phase, Lesson? Current, Lesson? Next, DateTime PhaseStart, DateTime PhaseEnd)
{
    public double Progress(DateTime now)
    {
        var total = (PhaseEnd - PhaseStart).TotalSeconds;
        return total <= 0 ? 0 : Math.Clamp((now - PhaseStart).TotalSeconds / total, 0, 1);
    }
}

internal static class SchoolSchedule
{
    public static readonly TimeSpan LessonLength = TimeSpan.FromMinutes(50);

    // Lesson ends; every lesson starts exactly LessonLength earlier.
    private static readonly TimeSpan[] Ends =
    {
        new(8, 35, 0), new(9, 30, 0), new(10, 35, 0), new(11, 30, 0), new(12, 25, 0), new(13, 20, 0),
        new(14, 15, 0), new(15, 10, 0), new(16, 10, 0), new(17, 5, 0), new(18, 0, 0)
    };

    public static readonly IReadOnlyList<Lesson> Lessons =
        Ends.Select((end, index) => new Lesson(index + 1, end - LessonLength, end)).ToList();

    /// <summary>
    /// Real lesson slots of a day from the class timetable, or null when the timetable does not
    /// cover that day; then every slot of a weekday counts (offline fallback).
    /// </summary>
    public static Func<DateTime, IReadOnlyList<Lesson>?>? DayLessons { get; set; }

    public static IReadOnlyList<Lesson> LessonsOn(DateTime date) =>
        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? Array.Empty<Lesson>()
            : DayLessons?.Invoke(date.Date) ?? Lessons;

    public static bool IsSchoolDay(DateTime date) => LessonsOn(date).Count > 0;

    public static bool IsLessonEnd(DateTime minute) =>
        LessonsOn(minute).Any(lesson => lesson.End == minute.TimeOfDay);

    public static bool IsLessonStart(DateTime minute) =>
        LessonsOn(minute).Any(lesson => lesson.Start == minute.TimeOfDay);

    public static SchoolState GetState(DateTime now)
    {
        var today = now.Date;
        var time = now.TimeOfDay;

        var lessons = LessonsOn(today);
        if (lessons.Count == 0)
            return FreeUntilNextSchoolDay(SchoolPhase.Weekend, now);

        var first = lessons[0];
        if (time < first.Start)
            return new SchoolState(SchoolPhase.BeforeSchool, null, first, today, today + first.Start);

        for (var i = 0; i < lessons.Count; i++)
        {
            var lesson = lessons[i];
            var next = i + 1 < lessons.Count ? lessons[i + 1] : null;

            if (time >= lesson.Start && time < lesson.End)
                return new SchoolState(SchoolPhase.Lesson, lesson, next, today + lesson.Start, today + lesson.End);

            if (next is not null && time >= lesson.End && time < next.Start)
                return new SchoolState(SchoolPhase.Break, lesson, next, today + lesson.End, today + next.Start);
        }

        return FreeUntilNextSchoolDay(SchoolPhase.AfterSchool, now);
    }

    private static SchoolState FreeUntilNextSchoolDay(SchoolPhase phase, DateTime now)
    {
        var day = now.Date.AddDays(1);
        for (var i = 0; i < 21 && !IsSchoolDay(day); i++) day = day.AddDays(1);
        var nextLessons = LessonsOn(day);
        var first = nextLessons.Count > 0 ? nextLessons[0] : Lessons[0];
        var today = LessonsOn(now.Date);
        var start = phase == SchoolPhase.AfterSchool && today.Count > 0 ? now.Date + today[^1].End : now.Date;
        return new SchoolState(phase, null, first, start, day + first.Start);
    }
}
