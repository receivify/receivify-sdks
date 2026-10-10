<!doctype html>
<html>
<head>
  <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
  <title>Order {{ $order->id }}</title>
  @if ($order->status !== 'paid')<meta http-equiv="refresh" content="5">@endif
  <style>body{font:16px/1.5 system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}.status{font-weight:700}.paid{color:#4d7c0f}.muted{color:#71717a;font-size:.9rem}</style>
</head>
<body>
  {{-- Display only: this page shows what the signed webhook recorded. --}}
  <h1>Order {{ $order->id }}</h1>
  <p>{{ $order->product }}: £{{ $order->amount }}</p>
  <p class="status {{ $order->status }}">{{ $order->status }}</p>
  @if ($order->status !== 'paid')<p class="muted">Refreshes every 5 seconds.</p>@endif
  <p><a href="/">Back to the shop</a></p>
</body>
</html>
