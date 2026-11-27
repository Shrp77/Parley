# Implementation Plan: Answer in the conversation

**Branch**: `031-answer-in-the-conversation` | **Date**: 2026-11-02 | **Spec**: [spec.md](spec.md)

## Summary

The conversation page already lists what waits on the person, with links, and offers Reply on a Clarify. This widens Reply to a Request, adds an outcome and a close beside it, adds a next button, anchors every message in a merged turn, and links the Inbox to the message. The terminal gets a key for next. No wire or schema change beyond `close_request`, from feature 029, which this branch is built on.

## Constitution Check

- **A person's line is a new Request, except a reply to a question the person chose to answer** (SPEC 5.6): the person presses Reply on the message. A Clarify on an agent's Request is what the Inbox already sends.
- **Forms bind `[FromForm]`**: `Outcome`, `Closing`.
- **Hold screens to what is drawn**: the web tests read the page, the terminal test the selected row.

## A trap

The transcript is compared by a digest of its rendered HTML. A form inside it carries an antiforgery token that differs at every render, so the region would be replaced every three seconds and move the reader. The close button is therefore a plain button, and its form sits outside the region. A test holds it.
