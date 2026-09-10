"""Every method: the request it makes, and what it makes of the answer."""

from __future__ import annotations

import unittest

from support import FakeAPI, Reply, envelope

from veruapis import Attendee, Event, SendMessage, VeruApi


class MailTest(unittest.TestCase):
    def setUp(self) -> None:
        self.api = FakeAPI()
        self.addCleanup(self.api.close)

    def test_list_folders(self) -> None:
        self.api.replies.append(
            Reply(200, envelope([{"id": "f1", "name": "INBOX", "message_count": 3, "unread_count": 1}]))
        )

        folders = self.api.client().mail.list_folders()

        self.assertEqual(self.api.last.route, "GET /v1/folders")
        self.assertEqual(folders[0].name, "INBOX")
        self.assertEqual(folders[0].unread_count, 1)

    def test_list_messages_sends_the_filter_and_returns_meta(self) -> None:
        self.api.replies.append(
            Reply(
                200,
                envelope(
                    [
                        {
                            "id": "m1",
                            "subject": "hello",
                            "from": "a@b.example",
                            "flags": {"read": True},
                            "labels": ["work"],
                        }
                    ],
                    next_cursor="c2",
                ),
            )
        )

        messages, meta = self.api.client().mail.list_messages(
            folder_id=["f1", "f2"], label=["work"], flag="starred", unread=True, limit=5
        )

        request = self.api.last
        self.assertEqual(request.route, "GET /v1/messages")
        self.assertEqual(request.query["folder_id"], ["f1", "f2"])
        self.assertEqual(request.query["label"], ["work"])
        self.assertEqual(request.query["flag"], ["starred"])
        self.assertEqual(request.query["unread"], ["true"])
        self.assertEqual(request.query["limit"], ["5"])

        # "from" is a keyword, so the field is from_. The wire name is mapped
        # in one place rather than at every use.
        self.assertEqual(messages[0].from_, "a@b.example")
        self.assertTrue(messages[0].flags.read)
        self.assertFalse(messages[0].flags.starred)
        self.assertEqual(messages[0].labels, ["work"])
        self.assertEqual(meta.next_cursor, "c2")

    def test_get_message_decodes_the_nested_shapes(self) -> None:
        self.api.replies.append(
            Reply(
                200,
                envelope(
                    {
                        "id": "m1",
                        "subject": "hello",
                        "from": {"email": "a@b.example", "name": "A"},
                        "to": [{"email": "c@d.example"}],
                        "headers": {"X-Thing": "1"},
                        "attachments": [
                            {"part_id": "2", "filename": "r.pdf", "size": 10, "inline": False}
                        ],
                    }
                ),
            )
        )

        message = self.api.client().mail.get_message("m1")

        self.assertEqual(self.api.last.route, "GET /v1/messages/m1")
        self.assertEqual(message.from_.name, "A")
        self.assertEqual(message.to[0].email, "c@d.example")
        self.assertEqual(message.headers["X-Thing"], "1")
        self.assertEqual(message.attachments[0].filename, "r.pdf")

    def test_get_message_survives_a_field_it_has_never_heard_of(self) -> None:
        # The API adds fields within a version. A client that raised on an
        # unfamiliar key would break every time the server learned something.
        self.api.replies.append(
            Reply(200, envelope({"id": "m1", "subject": "hi", "invented_last_week": {"a": 1}}))
        )

        message = self.api.client().mail.get_message("m1")
        self.assertEqual(message.subject, "hi")

    def test_list_attachments(self) -> None:
        self.api.replies.append(Reply(200, envelope([{"part_id": "2", "filename": "r.pdf"}])))

        attachments = self.api.client().mail.list_attachments("m1")

        self.assertEqual(self.api.last.route, "GET /v1/messages/m1/attachments")
        self.assertEqual(attachments[0].part_id, "2")

    def test_list_drafts(self) -> None:
        self.api.client().mail.list_drafts(limit=3, cursor="c1")

        self.assertEqual(self.api.last.route, "GET /v1/drafts")
        self.assertEqual(self.api.last.query["limit"], ["3"])
        self.assertEqual(self.api.last.query["cursor"], ["c1"])

    def test_send_takes_keywords_or_a_message(self) -> None:
        client = self.api.client()

        client.mail.send(to=["a@b.example"], subject="one", text="body")
        first = self.api.last.body

        client.mail.send(SendMessage(to=["a@b.example"], subject="one", text="body"))
        second = self.api.last.body

        self.assertEqual(first, second, "both spellings should put the same thing on the wire")
        self.assertEqual(self.api.last.route, "POST /v1/messages/send")

    def test_send_refuses_both_at_once(self) -> None:
        # Silently ignoring one of them would send a message the caller did not
        # write, which is the worst way to resolve an ambiguity.
        with self.assertRaises(TypeError):
            self.api.client().mail.send(SendMessage(to=["a@b.example"]), subject="other")

    def test_send_leaves_out_what_was_not_set(self) -> None:
        # Sending every default would make a partial update a full replacement,
        # and would fill the wire with empty strings on every call.
        self.api.replies.append(Reply(202, envelope({"id": "m9"})))

        sent = self.api.client().mail.send(to=["a@b.example"], subject="one")

        self.assertEqual(sent.id, "m9")
        self.assertEqual(set(self.api.last.body), {"to", "subject"})

    def test_send_carries_attachments(self) -> None:
        from veruapis import OutgoingAttachment

        self.api.client().mail.send(
            to=["a@b.example"],
            attachments=[
                OutgoingAttachment(filename="r.pdf", content_type="application/pdf", content_base64="AA==")
            ],
        )

        self.assertEqual(self.api.last.body["attachments"][0]["filename"], "r.pdf")
        self.assertNotIn("content_id", self.api.last.body["attachments"][0])


class CalendarTest(unittest.TestCase):
    def setUp(self) -> None:
        self.api = FakeAPI()
        self.addCleanup(self.api.close)

    def test_list_calendars(self) -> None:
        self.api.replies.append(
            Reply(200, envelope([{"id": "c1", "name": "Work", "is_owner": True}]))
        )

        calendars = self.api.client().calendar.list_calendars()

        self.assertEqual(self.api.last.route, "GET /v1/calendars")
        self.assertTrue(calendars[0].is_owner)

    def test_get_calendar(self) -> None:
        self.api.replies.append(Reply(200, envelope({"id": "c1", "name": "Work"})))

        calendar = self.api.client().calendar.get_calendar("c1")

        self.assertEqual(self.api.last.route, "GET /v1/calendars/c1")
        self.assertEqual(calendar.name, "Work")

    def test_list_events_requires_a_window(self) -> None:
        # A recurring series expands without limit, so there is no finite
        # answer to "all events" and the window is not optional.
        with self.assertRaises(TypeError):
            self.api.client().calendar.list_events()  # type: ignore[call-arg]

    def test_list_events(self) -> None:
        self.api.replies.append(
            Reply(
                200,
                envelope(
                    [
                        {
                            "id": "e1",
                            "summary": "Standup",
                            "starts_at": "2026-01-01T09:00:00Z",
                            "attendees": [{"email": "a@b.example", "role": "CHAIR"}],
                        }
                    ]
                ),
            )
        )

        events, _ = self.api.client().calendar.list_events(
            start="s", end="e", calendar_id=["c1"], limit=2
        )

        self.assertEqual(self.api.last.route, "GET /v1/events")
        self.assertEqual(self.api.last.query["start"], ["s"])
        self.assertEqual(self.api.last.query["calendar_id"], ["c1"])
        self.assertEqual(events[0].attendees[0].role, "CHAIR")

    def test_events_walks_the_pages(self) -> None:
        api = FakeAPI(
            Reply(200, envelope([{"id": "e1"}], next_cursor="c2")),
            Reply(200, envelope([{"id": "e2"}])),
        )
        self.addCleanup(api.close)

        ids = [e.id for e in api.client().calendar.events(start="s", end="e")]
        self.assertEqual(ids, ["e1", "e2"])

    def test_get_event(self) -> None:
        self.api.replies.append(Reply(200, envelope({"id": "e1", "summary": "Standup"})))

        event = self.api.client().calendar.get_event("c1", "e1")

        self.assertEqual(self.api.last.route, "GET /v1/calendars/c1/events/e1")
        self.assertEqual(event.summary, "Standup")

    def test_create_event(self) -> None:
        self.api.replies.append(Reply(200, envelope({"id": "e1", "summary": "Standup"})))

        self.api.client().calendar.create_event(
            "c1",
            Event(
                summary="Standup",
                starts_at="2026-01-01T09:00:00Z",
                ends_at="2026-01-01T09:15:00Z",
                attendees=[Attendee(email="a@b.example", role="CHAIR")],
            ),
        )

        request = self.api.last
        self.assertEqual(request.route, "POST /v1/calendars/c1/events")
        self.assertEqual(request.body["summary"], "Standup")
        self.assertEqual(request.body["attendees"][0]["email"], "a@b.example")
        self.assertTrue(request.headers["idempotency-key"], "a retried booking must not double-book")

    def test_update_event_sends_only_what_changed(self) -> None:
        self.api.replies.append(Reply(200, envelope({"id": "e1"})))

        self.api.client().calendar.update_event("c1", "e1", Event(summary="Moved"))

        self.assertEqual(self.api.last.route, "PATCH /v1/calendars/c1/events/e1")
        self.assertEqual(self.api.last.body, {"summary": "Moved"})

    def test_delete_event(self) -> None:
        self.api.replies.append(Reply(204))

        self.api.client().calendar.delete_event("c1", "e1")

        self.assertEqual(self.api.last.route, "DELETE /v1/calendars/c1/events/e1")
        self.assertNotIn("idempotency-key", self.api.last.headers)

    def test_free_busy(self) -> None:
        self.api.replies.append(
            Reply(
                200,
                envelope([{"email": "a@b.example", "busy": [{"start": "s", "end": "e"}]}]),
            )
        )

        busy = self.api.client().calendar.free_busy(["a@b.example"], "s", "e")

        self.assertEqual(self.api.last.route, "POST /v1/freebusy")
        self.assertEqual(self.api.last.body, {"emails": ["a@b.example"], "start": "s", "end": "e"})
        self.assertEqual(busy[0].busy[0].start, "s")


class ShapeTest(unittest.TestCase):
    """The two rules the decoder is built on."""

    def test_a_missing_field_takes_its_default_rather_than_failing(self) -> None:
        from veruapis.types import Folder, decode

        folder = decode(Folder, {"id": "f1"})
        self.assertEqual(folder.name, "")
        self.assertEqual(folder.message_count, 0)

    def test_null_decodes_to_nothing(self) -> None:
        from veruapis.types import Message, decode

        self.assertIsNone(decode(Message, None))

    def test_a_scalar_where_an_object_was_expected_is_left_alone(self) -> None:
        # Better to hand the caller what arrived than to invent an empty object
        # and let them wonder why every field is blank.
        from veruapis.types import Folder, decode

        self.assertEqual(decode(Folder, "surprise"), "surprise")

    def test_encode_drops_what_was_never_set(self) -> None:
        # The same rule as Go's omitempty, so both clients put the same bytes
        # on the wire for the same call. See DECISIONS.md for the one edge this
        # has: a false cannot be told from an unset boolean.
        from veruapis.types import Event, encode

        self.assertEqual(encode(Event(summary="x")), {"summary": "x"})
        self.assertEqual(encode({"a": [1, 2]}), {"a": [1, 2]})


class DriveTest(unittest.TestCase):
    """The two calls whose bodies are not JSON."""

    def test_a_part_is_sent_as_bytes_rather_than_json(self) -> None:
        with FakeAPI() as api:
            api.default = Reply(200, {"data": {"part": 2, "etag": "e"}, "meta": {"request_id": "r"}})
            receipt = api.client().files.upload_part("u1", 2, b"\x00\xff\x10")

        # Encoding a file as JSON would inflate it and corrupt anything that is
        # not valid UTF-8, which is most of what people upload.
        self.assertEqual(api.requests[0].body, b"\x00\xff\x10")
        self.assertEqual(
            api.requests[0].headers.get("content-type"), "application/octet-stream"
        )
        self.assertEqual(receipt.part, 2)

    def test_a_download_returns_bytes_and_carries_a_range(self) -> None:
        with FakeAPI() as api:
            api.default = Reply(200, raw=b"%PDF", headers={"Content-Type": "application/pdf"})
            raw, content_type = api.client().files.download("b1", range="bytes=0-3")

        self.assertEqual(raw, b"%PDF")
        self.assertEqual(content_type, "application/pdf")
        self.assertEqual(api.requests[0].headers.get("range"), "bytes=0-3")

    def test_moving_to_the_root_is_not_the_same_as_leaving_it_alone(self) -> None:
        with FakeAPI() as api:
            api.default = Reply(200, {"data": {"id": "b1"}, "meta": {"request_id": "r"}})
            client = api.client()
            client.files.update_file("b1", folder_id="o2")
            client.files.update_file("b1", move_to_root=True)
            client.files.update_file("b1", name="f.pdf")

        self.assertEqual(api.requests[0].body, {"folder_id": "o2"})
        self.assertEqual(api.requests[1].body, {"folder_id": ""})
        # A rename alone says nothing about where the file lives, which is the
        # case a nullable field cannot express on its own.
        self.assertEqual(api.requests[2].body, {"name": "f.pdf"})

if __name__ == "__main__":
    unittest.main()
