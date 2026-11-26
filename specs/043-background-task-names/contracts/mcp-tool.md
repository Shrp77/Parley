# Contract: `parley_describe_background` (parley-mcp)

## Description, as the agent reads it

> Tell the person, in plain words, what a background task you started is doing. Call it right after you start a
> background sub-agent or a background command, with the task's id from the tool's result.
>
>   name: what is being done, verb first, in the person's terms, at most 64 characters. "Checking VAT on gift cards",
>         not "Investigating the tax module's handling of stored-value instruments".
>   description: optional. One or two short sentences on what it is for. At most 300 characters. No background, no
>         narrative.
>   link: optional. Where the person can read more: `exchange #N` for the work it is for, `conversation:<id>`,
>         `file:<name>` for a file placed in Parley, or an https address of a ticket or document.
>
> Only the person sees what you write here; no other agent does. It is kept until your background work ends.

## Parameters

| Name | Required | Type |
|---|---|---|
| `task_id` | yes | string |
| `name` | yes | string |
| `description` | no | string |
| `link` | no | string |

## Result

`{ "ok": true, "kept": true }`, with `"name_cut": true` or `"description_cut": true` when cut, and
`"link_dropped": "<why>"` when a link was not kept. When not kept, as the other tools refuse:
`{ "ok": false, "refused": "notkept", "why": "Nothing was kept. <why>" }`. Nothing in it says whether the task runs.

## Server instructions

One line joins the existing bullets: "When you start a background sub-agent or command, say what it is doing with
parley_describe_background, in a few plain words, so the person can see it."
