# Opening the web console from your phone or tablet

Parley's web console runs on the computer where Parley runs, and on purpose it only listens to that
computer. That keeps strangers out, but it also means your phone can't reach it on its own.

[Tailscale](https://tailscale.com) is a free app that links your own devices into a small private
network of their own, called a *tailnet*. With it, you can open the console on your phone while
you are away from your desk, check what your agents are working on, and answer a question one of
them asked you, without opening the computer to the internet.

This page takes you from nothing installed to the console open on your phone. It takes about
fifteen minutes. You do six things:

1. Install Tailscale on the Parley computer and on your phone.
2. Turn on HTTPS for your tailnet.
3. Find your computer's Tailscale name.
4. Tell Tailscale to pass that name on to the console.
5. Tell Parley the name is yours.
6. Open the name on your phone.

Step 5 is the one people miss. Until Parley knows the name, every page answers **400 Bad Request**.

## Before you start

- Parley is installed and its web console opens on the computer itself. Check by running
  `parley web-ui --open` there. Your browser should show the console.
- You can open a terminal on that computer: PowerShell or Windows Terminal on Windows, Terminal on
  a Mac or on Linux.

The console normally listens at `http://127.0.0.1:5280`. If you gave it another address when you
set Parley up, use that port number wherever this page says `5280`. `parley autostart status` shows
the address.

## 1. Install Tailscale and sign in

**On the Parley computer:** download Tailscale from [tailscale.com/download](https://tailscale.com/download),
install it and sign in. Any of the sign-in choices it offers works: Google, Microsoft, GitHub or an email.

**On your phone or tablet:** install Tailscale from the App Store or Google Play. Sign in with
**the same account** you used on the computer, then switch Tailscale on. The phone asks once whether
Tailscale may add a VPN. Allow it.

Both devices are now on your tailnet. In the Tailscale app on your phone, the computer should
appear in the device list.

## 2. Turn on HTTPS for your tailnet

Your phone's browser will only use the console over HTTPS, the padlock kind of address. Tailscale
provides the certificate for it, but you have to switch that on once.

1. Open the Tailscale admin console at [login.tailscale.com/admin/dns](https://login.tailscale.com/admin/dns)
   and sign in with the same account.
2. Check that **MagicDNS** is on. It is on for new tailnets. It gives each device a name instead
   of a number.
3. Under **HTTPS Certificates**, choose **Enable HTTPS**.

If you skip this step, step 4 stops and prints a link to this same page.

## 3. Find your computer's Tailscale name

Each device on your tailnet has a full name ending in `.ts.net`, such as
`devbox.tail1234.ts.net`. The first part is the computer's name and the middle part is your
tailnet's.

To see it, run this in a terminal on the Parley computer:

```
tailscale status
```

The first line is this computer. Its full name is that short name, then a dot, then your tailnet's
name. You can also find the full name on the **Machines** page of the admin console: click the
computer and look for **Full domain**.

Write the full name down. The rest of this page calls it **your name**, and uses
`devbox.tail1234.ts.net` as the example.

## 4. Pass the name on to the console

This tells Tailscale to answer `https://your name` and hand each request to the console on this
computer. In a terminal on the Parley computer, run:

```
tailscale serve --bg 5280
```

- `--bg` keeps it running after you close the terminal, and after a restart of the computer.
- On Linux, put `sudo` in front of the command.
- If it says **Access is denied** on Windows, open the terminal with **Run as administrator** and
  run it again.

To check what it set up, run `tailscale serve status`. It should say:

```
https://devbox.tail1234.ts.net (tailnet only)
|-- / proxy http://127.0.0.1:5280
```

The words **tailnet only** matter. They mean only your own devices can reach it.

If you open your name now, you will see **400 Bad Request**. That is expected until step 5.

## 5. Tell Parley the name is yours

The console refuses every name it does not know. That is what stops a website you visit from
reaching it. So you list your name in Parley's `settings.json` file.

The file is in Parley's own folder:

| On | The file |
|---|---|
| Windows | `%APPDATA%\Parley\settings.json`. Paste `%APPDATA%\Parley` into File Explorer's address bar to open the folder. |
| macOS and Linux | `~/.config/Parley/settings.json` |

If there is no `settings.json` yet, create one. Open it in a plain text editor such as Notepad or
TextEdit, and make it say this, with your own name in place of the example:

```json
{
  "webUi": {
    "hosts": [ "devbox.tail1234.ts.net" ]
  }
}
```

If the file already has other settings, keep them, and add the `"webUi"` part beside them, with a
comma between the parts.

Write the name out in full, as you found it in step 3. Parley leaves out anything else: no
`https://` in front, no port, no `*` wildcard and no IP address.

**Then restart Parley's broker, so the console reads the file.** The easiest way is to log out of
the computer and back in. On Windows you can instead open Task Scheduler, find the task called
**parley-broker**, choose **End**, then **Run**.

When the broker starts, its log, `parley-broker.log` in the same folder as `settings.json`, says
which names the console now answers to:

```
Parley console: also answering to devbox.tail1234.ts.net (settings.json, webUi.hosts). Anyone who
can reach those names acts as you: the console has no sign-in.
```

## 6. Open the console on your phone

With Tailscale switched on on your phone, open its browser and go to:

```
https://devbox.tail1234.ts.net
```

The first visit can take a few seconds while Tailscale fetches the certificate. Then you see the
same console as on the computer: your projects, your agents, and your Inbox with anything waiting
for you. A green dot beside **human-user** at the top means you have taken your seat.

Add it to your home screen, and next time it is one tap.

## If it doesn't work

| What you see | Why, and what to do |
|---|---|
| **400 Bad Request** | The console doesn't know the name. Check step 5: the name is spelled exactly as in step 3, the file is in the right folder, and the broker was restarted afterwards. |
| *This console answers only its own pages, and an address typed or bookmarked.* | You opened it over plain `http://`, or Tailscale was set up with `--http`. Use `https://`, and set it up as in step 4. |
| The broker's log says a name **was left out** | That entry is not a whole name: it has a `*`, an IP address, a port or `https://` in it. Write the full name, as in step 3. |
| A certificate warning, or the page never loads on the first visit | HTTPS is not on for your tailnet. Do step 2, then run the command in step 4 again. |
| The page can't be found or times out | Tailscale is off on your phone, or the phone is signed in to a different account. Open the Tailscale app, switch it on, and check that the computer appears in its list. |
| It worked, then stopped | The Parley computer is asleep or off, or the broker isn't running. On the computer, `parley autostart status` says whether the broker is set up to start. |

## Keep it private

- **Anyone who can open your name can do anything you can do in the console.** The console has no
  password of its own. Who can reach it is up to your tailnet. If you share your tailnet with
  other people, use Tailscale's access controls so only your own devices can reach the Parley
  computer.
- **Never use Tailscale Funnel, or any other tunnel that is open to the internet, for the console.**
  Funnel would put it on the public internet, and anyone who found the address could act as you.
  `tailscale serve` without Funnel stays **tailnet only**.
- **Give the console a name of its own.** Don't serve other things under the same name. Anything
  else served there counts as one of the console's own pages, so it could act through the console.
- **Only the console, never the broker.** The broker listens on a port of its own, usually from
  19100 up. Never pass that port on with Tailscale, or open it to any network. Anything that can
  reach the broker controls every project, with no sign-in. An agent on another computer reaches
  the broker through an SSH tunnel instead. See
  [Reaching a broker from another machine](configuration.md#reaching-a-broker-from-another-machine).

To stop using remote access, run `tailscale serve --https=443 off` on the Parley computer, and take
the name out of `settings.json`.
