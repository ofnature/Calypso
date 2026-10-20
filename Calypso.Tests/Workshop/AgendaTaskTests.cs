using Calypso.Core.Workshop;
using Calypso.Workshop;

namespace Calypso.Tests.Workshop;

public class AgendaTaskTests
{
    private static WeekPlan PlanWithRest(params int[] restDays) => new()
    {
        Cycles = Enumerable.Range(0, 7).Select(d => new CyclePlan { Day = d, Rest = restDays.Contains(d) }).ToList(),
    };

    [Fact]
    public void RestMaskKeepsPastDaysAndNextWeekAndTakesThePlanForTheRest()
    {
        // Current: rest on C1 (bit 0), a stale rest on C5 (bit 4), next week C1 (bit 7).
        const uint current = (1u << 0) | (1u << 4) | (1u << 7);
        var plan = PlanWithRest(0, 6); // plan rests C1 and C7

        var mask = WorkshopAgendaTask.RestMask(current, plan, firstDay: 2);

        Assert.Equal((1u << 0) | (1u << 6) | (1u << 7), mask);
    }

    [Fact]
    public void RestMaskFromTheFirstDayReplacesTheWholeWeek()
    {
        var mask = WorkshopAgendaTask.RestMask(0b1111111, PlanWithRest(3), firstDay: 0);
        Assert.Equal(1u << 3, mask);
    }
}
