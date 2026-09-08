"""The transport: what goes on the wire, and what happens when it fails."""

from __future__ import annotations

import unittest
from typing import Any

from support import FakeAPI, Recorded, Reply, envelope, refusal

from veruapis import VeruApi, VeruApiError
from veruapis._client import _backoff, _query_string


class RequestTest(unittest.TestCase):
    def setUp(self) -> None:
        self.api = FakeAPI()
        self.addCleanup(self.api.close)

    def test_a_key_is_required(self) -> None:
        # Failing at construction rather than at the first call: an empty
        # environment variable is a configuration mistake, and finding out
        # about it halfway through a script is finding out too late.
        for bad in ("", "   "):
            with self.assertRaises(ValueError):
                VeruApi(bad)

    def test_the_key_travels_as_a_bearer_token(self) -> None:
        client = self.api.client()
        client.mail.list_folders()

        self.assertEqual(
            self.api.last.headers["authorization"], "Bearer vak_live_test_secret"
        )
        self.assertEqual(self.api.last.headers["accept"], "application/json")

    def test_writes_carry_an_idempotency_key_and_reads_do_not(self) -> None:
        client = self.api.client()

        client.mail.list_folders()
        self.assertNotIn("idempotency-key", self.api.last.headers)

        client.mail.send(to=["a@b.example"], subject="hello")
        self.assertTrue(self.api.last.headers["idempotency-key"])

    def test_a_supplied_idempotency_key_is_used_verbatim(self) -> None:
        client = self.api.client()
        client.mail.send(to=["a@b.example"], idempotency_key="mine-1")

        self.assertEqual(self.api.last.headers["idempotency-key"], "mine-1")

    def test_one_idempotency_key_survives_every_retry(self) -> None:
        # The whole point of the header. A retry that generated a new key would
        # be a second request as far as the server is concerned, and the caller
        # would have sent two emails to avoid sending two emails.
        api = FakeAPI(
            Reply(500, refusal("internal_error")),
            Reply(500, refusal("internal_error")),
            Reply(202, envelope({"id": "m1"})),
        )
        self.addCleanup(api.close)

        api.client().mail.send(to=["a@b.example"])

        keys = {r.headers["idempotency-key"] for r in api.requests}
        self.assertEqual(len(api.requests), 3)
        self.assertEqual(len(keys), 1, f"a new key per attempt: {keys}")

    def test_a_body_is_sent_as_json(self) -> None:
        client = self.api.client()
        client.mail.send(to=["a@b.example"], subject="hi", text="there")

        self.assertEqual(self.api.last.headers["content-type"], "application/json")
        self.assertEqual(
            self.api.last.body, {"to": ["a@b.example"], "subject": "hi", "text": "there"}
        )


class RetryTest(unittest.TestCase):
    def test_429_408_and_5xx_are_retried(self) -> None:
        for status in (408, 429, 500, 503):
            with self.subTest(status=status):
                api = FakeAPI(Reply(status, refusal("busy")), Reply(200, envelope([])))
                self.addCleanup(api.close)

                api.client().mail.list_folders()
                self.assertEqual(len(api.requests), 2)

    def test_a_refusal_that_would_fail_again_is_not_retried(self) -> None:
        # A 400 or a 403 fails the same way however many times it is sent.
        # Retrying one only delays the error the caller has to handle.
        for status in (400, 401, 403, 404, 409, 422):
            with self.subTest(status=status):
                api = FakeAPI(Reply(status, refusal("no")))
                self.addCleanup(api.close)

                with self.assertRaises(VeruApiError):
                    api.client().mail.list_folders()
                self.assertEqual(len(api.requests), 1)

    def test_retries_stop_at_the_limit(self) -> None:
        api = FakeAPI(*[Reply(500, refusal("internal_error")) for _ in range(9)])
        self.addCleanup(api.close)

        with self.assertRaises(VeruApiError) as caught:
            api.client(max_retries=3).mail.list_folders()

        self.assertEqual(len(api.requests), 4, "the first attempt plus three retries")
        self.assertEqual(caught.exception.status, 500)

    def test_retrying_can_be_turned_off(self) -> None:
        api = FakeAPI(Reply(503, refusal("unavailable")))
        self.addCleanup(api.close)

        with self.assertRaises(VeruApiError):
            api.client(max_retries=0).mail.list_folders()
        self.assertEqual(len(api.requests), 1)

    def test_retry_after_is_honoured_over_the_backoff(self) -> None:
        # The server knows when it will be ready and the client does not, so a
        # number it supplies beats one this library made up.
        api = FakeAPI(
            Reply(429, refusal("rate_limited"), headers={"Retry-After": "7"}),
            Reply(200, envelope([])),
        )
        self.addCleanup(api.close)

        client = api.client()
        client.mail.list_folders()

        self.assertEqual(api.slept, [7.0])

    def test_a_retry_after_date_is_honoured_too(self) -> None:
        # The RFC allows both spellings, and something upstream will use each.
        api = FakeAPI(
            Reply(
                429,
                refusal("rate_limited"),
                headers={"Retry-After": "Wed, 21 Oct 2099 07:28:00 GMT"},
            ),
            Reply(200, envelope([])),
        )
        self.addCleanup(api.close)

        client = api.client()
        client.mail.list_folders()

        self.assertEqual(len(api.slept), 1)
        self.assertGreater(api.slept[0], 0)

    def test_an_unreadable_retry_after_falls_back_to_the_backoff(self) -> None:
        api = FakeAPI(
            Reply(429, refusal("rate_limited"), headers={"Retry-After": "soon"}),
            Reply(200, envelope([])),
        )
        self.addCleanup(api.close)

        client = api.client()
        client.mail.list_folders()

        self.assertEqual(len(api.slept), 1)
        self.assertGreater(api.slept[0], 0)

    def test_an_unreachable_server_is_retried_then_reported(self) -> None:
        api = FakeAPI()
        base = api.base_url
        api.close()  # nothing is listening now

        client = VeruApi("vak_live_test_secret", base_url=base, max_retries=1)
        slept: list = []
        client._sleep = slept.append

        with self.assertRaises(VeruApiError) as caught:
            client.mail.list_folders()

        self.assertEqual(caught.exception.code, "transport_error")
        self.assertEqual(len(slept), 1, "one retry before giving up")

    def test_backoff_grows_and_is_capped(self) -> None:
        self.assertLessEqual(_backoff(0), 0.5)
        self.assertLessEqual(_backoff(1), 1.0)

        # Capped, so a long-lived process does not end up sleeping for minutes.
        self.assertLessEqual(_backoff(30), 8.0)

        # Jittered, so a fleet of clients does not retry in step and turn a
        # brief failure into a sustained one.
        samples = {_backoff(4) for _ in range(50)}
        self.assertGreater(len(samples), 1)


class ErrorTest(unittest.TestCase):
    def test_the_envelope_is_read_into_the_error(self) -> None:
        api = FakeAPI(
            Reply(
                403,
                refusal(
                    "insufficient_scope",
                    "This key cannot send mail.",
                    details=[{"field": "scope", "message": "mail.send"}],
                ),
            )
        )
        self.addCleanup(api.close)

        with self.assertRaises(VeruApiError) as caught:
            api.client().mail.list_folders()

        err = caught.exception
        self.assertEqual(err.code, "insufficient_scope")
        self.assertEqual(err.status, 403)
        self.assertEqual(err.request_id, "req_test")
        self.assertEqual(err.details[0].field, "scope")
        self.assertIn("insufficient_scope", str(err))
        self.assertIn("req_test", str(err))

    def test_the_four_questions_worth_asking(self) -> None:
        cases = {
            401: "is_auth_problem",
            403: "is_permission_problem",
            404: "is_not_found",
            429: "is_rate_limited",
        }

        for status, attribute in cases.items():
            with self.subTest(status=status):
                api = FakeAPI(Reply(status, refusal("x")))
                self.addCleanup(api.close)

                with self.assertRaises(VeruApiError) as caught:
                    api.client(max_retries=0).mail.list_folders()

                err = caught.exception
                self.assertTrue(getattr(err, attribute))

                # And exactly one of them is true, so a caller branching on the
                # wrong one finds out here rather than in production.
                others = [a for a in cases.values() if a != attribute]
                self.assertFalse(any(getattr(err, a) for a in others))

    def test_a_response_that_is_not_the_api_says_so(self) -> None:
        # A proxy, an error page, a misrouted request. Reporting that beats
        # reporting a JSON parse failure nobody can act on.
        for body in (b"<html>502 Bad Gateway</html>", b"", b'{"nope":true}'):
            with self.subTest(body=body):
                api = FakeAPI(Reply(502, raw=body))
                self.addCleanup(api.close)

                with self.assertRaises(VeruApiError) as caught:
                    api.client(max_retries=0).mail.list_folders()

                self.assertEqual(caught.exception.code, "unreadable_response")
                self.assertIn("502", caught.exception.message)

    def test_an_error_without_a_request_id_still_reads(self) -> None:
        api = FakeAPI(Reply(400, {"error": {"code": "bad_request", "message": "no"}}))
        self.addCleanup(api.close)

        with self.assertRaises(VeruApiError) as caught:
            api.client().mail.list_folders()

        self.assertEqual(str(caught.exception), "no (bad_request)")


class EnvelopeTest(unittest.TestCase):
    def test_204_has_no_body_to_decode(self) -> None:
        api = FakeAPI(Reply(204))
        self.addCleanup(api.close)

        api.client().calendar.delete_event("c1", "e1")
        self.assertEqual(api.last.method, "DELETE")

    def test_meta_comes_back_alongside_the_data(self) -> None:
        api = FakeAPI(Reply(200, envelope([], next_cursor="c2")))
        self.addCleanup(api.close)

        _, meta = api.client().mail.list_drafts()
        self.assertEqual(meta.next_cursor, "c2")
        self.assertEqual(meta.request_id, "req_test")

    def test_a_response_that_is_not_an_object_is_passed_through(self) -> None:
        api = FakeAPI(Reply(200, raw=b"[1,2,3]"))
        self.addCleanup(api.close)

        data, meta = api.client().request("GET", "/v1/folders")
        self.assertEqual(data, [1, 2, 3])
        self.assertEqual(meta.request_id, "")


class QueryTest(unittest.TestCase):
    def test_a_list_repeats_rather_than_joining(self) -> None:
        # The API reads ?folder_id=a&folder_id=b as two values and
        # ?folder_id=a,b as one folder called "a,b".
        self.assertEqual(
            _query_string({"folder_id": ["a", "b"]}), "folder_id=a&folder_id=b"
        )

    def test_empty_values_are_left_out(self) -> None:
        rendered = _query_string(
            {"a": "", "b": None, "c": 0, "d": False, "e": [], "f": "kept"}
        )
        self.assertEqual(rendered, "f=kept")

    def test_true_is_sent_as_the_word(self) -> None:
        self.assertEqual(_query_string({"unread": True}), "unread=true")

    def test_nothing_at_all_renders_to_nothing(self) -> None:
        self.assertEqual(_query_string(None), "")
        self.assertEqual(_query_string({}), "")

    def test_an_id_with_a_slash_is_still_one_id(self) -> None:
        api = FakeAPI()
        self.addCleanup(api.close)

        api.client().mail.get_message("a/b c")
        self.assertEqual(api.last.path, "/v1/messages/a%2Fb%20c")


class PaginationTest(unittest.TestCase):
    def test_the_cursor_is_followed_to_the_end(self) -> None:
        api = FakeAPI(
            Reply(200, envelope([{"id": "m1"}], next_cursor="c2")),
            Reply(200, envelope([{"id": "m2"}], next_cursor="c3")),
            Reply(200, envelope([{"id": "m3"}])),
        )
        self.addCleanup(api.close)

        ids = [m.id for m in api.client().mail.messages(folder_id=["f1"])]

        self.assertEqual(ids, ["m1", "m2", "m3"])
        self.assertEqual(api.requests[0].query.get("cursor"), None)
        self.assertEqual(api.requests[1].query["cursor"], ["c2"])
        self.assertEqual(api.requests[2].query["cursor"], ["c3"])

    def test_the_filter_is_carried_onto_every_page(self) -> None:
        api = FakeAPI(
            Reply(200, envelope([{"id": "m1"}], next_cursor="c2")),
            Reply(200, envelope([{"id": "m2"}])),
        )
        self.addCleanup(api.close)

        list(api.client().mail.messages(folder_id=["f1"], unread=True))

        for request in api.requests:
            self.assertEqual(request.query["folder_id"], ["f1"])
            self.assertEqual(request.query["unread"], ["true"])

    def test_an_empty_page_with_a_cursor_does_not_loop_forever(self) -> None:
        # The failure this guards against does not raise, it hangs, which is
        # the worst way for a client library to be wrong.
        api = FakeAPI(*[Reply(200, envelope([], next_cursor="always")) for _ in range(4)])
        self.addCleanup(api.close)

        self.assertEqual(list(api.client().mail.messages(folder_id=["f1"])), [])
        self.assertEqual(len(api.requests), 1)

    def test_a_caller_that_stops_early_stops_the_walk(self) -> None:
        api = FakeAPI(
            Reply(200, envelope([{"id": "m1"}, {"id": "m2"}], next_cursor="c2")),
            Reply(200, envelope([{"id": "m3"}])),
        )
        self.addCleanup(api.close)

        for message in api.client().mail.messages(folder_id=["f1"]):
            first = message
            break

        self.assertEqual(first.id, "m1")
        self.assertEqual(len(api.requests), 1, "the second page was never asked for")

    def test_a_failure_part_way_through_raises(self) -> None:
        api = FakeAPI(
            Reply(200, envelope([{"id": "m1"}], next_cursor="c2")),
            Reply(403, refusal("insufficient_scope")),
        )
        self.addCleanup(api.close)

        seen = []
        with self.assertRaises(VeruApiError):
            for message in api.client().mail.messages(folder_id=["f1"]):
                seen.append(message.id)

        self.assertEqual(seen, ["m1"], "what was yielded before the failure is kept")


class ConfigurationTest(unittest.TestCase):
    def test_a_trailing_slash_on_the_base_url_does_not_double_up(self) -> None:
        api = FakeAPI()
        self.addCleanup(api.close)

        VeruApi("k", base_url=api.base_url + "/").mail.list_folders()
        self.assertEqual(api.last.path, "/v1/folders")

    def test_the_default_base_url_is_production(self) -> None:
        self.assertEqual(VeruApi("k").base_url, "https://api.veruapis.com")

    def test_a_negative_retry_count_means_none(self) -> None:
        self.assertEqual(VeruApi("k", max_retries=-3).max_retries, 0)


if __name__ == "__main__":
    unittest.main()


class ErrorHelperTest(unittest.TestCase):
    """The corners of the error reader that a live server cannot reach."""

    def test_a_body_that_is_json_but_not_an_object(self) -> None:
        # A proxy answering with a bare array is not the API, and saying so is
        # more useful than a decode failure.
        from veruapis.errors import error_from_response

        err = error_from_response(502, None, b"[1,2,3]")
        self.assertEqual(err.code, "unreadable_response")

    def test_a_response_with_no_headers_at_all(self) -> None:
        from veruapis.errors import _retry_after

        self.assertIsNone(_retry_after(None))
        self.assertIsNone(_retry_after(object()))

    def test_a_retry_after_in_the_past_is_no_wait(self) -> None:
        # A date that has already gone by means "now", not "wait a negative
        # number of seconds", which is what arithmetic alone would give.
        from veruapis.errors import _retry_after

        self.assertIsNone(_retry_after({"Retry-After": "Wed, 21 Oct 2015 07:28:00 GMT"}))
        self.assertIsNone(_retry_after({"Retry-After": "-5"}))
        self.assertIsNone(_retry_after({"Retry-After": ""}))
