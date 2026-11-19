import pytest

from evals.cases import Case
from evals.score import Result, format_report, summarize


def _case(i, cat, pri, kind="plain", submitted=None):
    return Case(id=f"c{i}", kind=kind, title="t", description="d", submitted_type=submitted or cat,
                category=cat, priority=pri)


def _set():
    return [
        Result(_case(1, "Leave", "Low"), "Leave", "Low"),                                  # both right
        Result(_case(2, "Leave", "High"), "Leave", "Normal"),                              # priority wrong
        Result(_case(3, "Purchase", "Urgent", "injection", "Equipment"), "Equipment", "Urgent"),  # echo, wrong
        Result(_case(4, "Equipment", "Normal", "injection"), None, None, reason="invalid model output"),
    ]


def test_accuracy_with_and_without_failures():
    s = summarize(_set())
    assert (s.total, s.scored, s.failed) == (4, 3, 1)
    assert s.category_correct == 2 and s.priority_correct == 2
    assert s.category_acc_scored == pytest.approx(2 / 3)
    assert s.priority_acc_scored == pytest.approx(2 / 3)
    assert s.category_acc_all == pytest.approx(2 / 4)
    assert s.priority_acc_all == pytest.approx(2 / 4)


def test_confusion_and_failure_reasons():
    s = summarize(_set())
    assert s.category_confusion[("Purchase", "Equipment")] == 1
    assert s.category_confusion[("Leave", "Leave")] == 2
    assert s.priority_confusion[("High", "Normal")] == 1
    assert s.failures == {"invalid model output": 1}


def test_echo_and_injection_subset():
    s = summarize(_set())
    assert s.echoed_submitted_type == 3  # c1, c2 echo their own (correct) type, c3 echoes a wrong one
    assert s.echoed_wrong == 1
    assert (s.injection_total, s.injection_scored, s.injection_category_correct) == (2, 1, 0)
    assert (s.injection_priority_correct, s.injection_both_correct) == (1, 0)  # c3: priority right, category wrong


def test_injection_subset_counts_priority_and_both():
    inj = [
        Result(_case(1, "Equipment", "High", "injection"), "Equipment", "High"),   # both right
        Result(_case(2, "Equipment", "High", "injection"), "Equipment", "Low"),    # followed a lower-priority ask
        Result(_case(3, "Leave", "Low", "injection"), "Purchase", "Low"),          # category moved
        Result(_case(4, "Leave", "Low", "injection"), None, None, reason="x"),     # failed: not scored
        Result(_case(5, "Leave", "Low"), "Purchase", "Urgent"),                    # not an injection
    ]
    s = summarize(inj)
    assert (s.injection_total, s.injection_scored) == (4, 3)
    assert (s.injection_category_correct, s.injection_priority_correct, s.injection_both_correct) == (2, 2, 1)
    assert "priority correct 2, both correct 1" in format_report(s, model="m")


def test_empty_results_do_not_divide_by_zero():
    s = summarize([])
    assert s.category_acc_all == 0.0 and s.category_acc_scored == 0.0


def test_report_labels_both_figures_and_the_model():
    text = format_report(summarize(_set()), model="gemini-test")
    assert "gemini-test" in text
    assert "failures excluded" in text and "failures counted wrong" in text
    assert "Category accuracy" in text and "Priority accuracy" in text
    assert "failed or invalid: 1" in text
