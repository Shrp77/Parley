-- Parley schema, migration 011: protocol 0.7, a request is the ask.
-- Applied inside a transaction by SchemaMigrator; do not add BEGIN/COMMIT here.
--
-- SPEC.md section 5.1 reduced a Request to the ask and its conversation: the intent, the items with
-- their kinds, identifiers and gates, and the done_when are gone. Section 9.2 carries this schema and
-- SchemaConformanceTests compares them.
--
-- **No row is touched, and that is the whole design.** The old requirements live in one BEFORE INSERT
-- trigger, and a message is never updated (section 9.4 prunes text, it does not rewrite it). So
-- replacing the trigger decides what may be written from now on and says nothing about what was
-- written before: every Request and Result stored under 0.5 and 0.6 keeps its own fields, still
-- renders whole in a transcript and an export, and -- since nothing times out (section 14) -- an
-- exchange one of them opened may still be open and is still answered by its own item identifiers.
--
-- The alternative was rewriting stored payloads into the new shape. That would destroy words a
-- participant wrote, which is the one thing the log exists to keep.
--
-- tr_result_is_well_formed is left exactly as it is. It never named an item identifier: it requires
-- at least one outcome, holds a blocked outcome to naming its blocker, checks every source against
-- the registry, and lets only the broker write 'closed'. All four are as true of one outcome named
-- 'request' as of five named by a Request's items.

DROP TRIGGER tr_request_is_well_formed;

-- **The blank set is spelled out, and that was found by probe rather than by reading.** SQL's one-argument
-- trim() removes the space character and nothing else, so a first draft of this admitted an ask of one tab,
-- one line feed or one no-break space -- while both senders in this repository refuse all three, because
-- .NET's IsNullOrWhiteSpace does. A guard that is weaker than the code above it is the wrong way round:
-- section 9.3 puts the rule here precisely so a second implementation cannot route around it, and a Request
-- that says nothing is exactly what it exists to refuse.
--
-- The set is Unicode's whitespace as IsNullOrWhiteSpace has it, so the three places that decide an ask was
-- said agree: tab, line feed, vertical tab, form feed, carriage return, space, next line, no-break space,
-- ogham space, the en-quad block through hair space, line and paragraph separators, narrow no-break space,
-- medium mathematical space and ideographic space. U+200B is deliberately absent: a zero-width space is not
-- whitespace by that rule, and a guard stricter than the senders would refuse what they had already sent.
CREATE TRIGGER tr_request_is_well_formed BEFORE INSERT ON messages
WHEN NEW.type = 'request' AND json_valid(NEW.payload_json)
BEGIN
  SELECT RAISE(ABORT, 'a request carries an ask')
   WHERE json_type(NEW.payload_json, '$.ask') IS NOT 'text'
      OR length(trim(json_extract(NEW.payload_json, '$.ask'),
                     char(9, 10, 11, 12, 13, 32, 133, 160, 5760,
                          8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202,
                          8232, 8233, 8239, 8287, 12288))) = 0;
END;
