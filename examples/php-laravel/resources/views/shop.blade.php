<!doctype html>
<html>
<head>
  <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
  <title>Shop</title>
  <style>body{font:16px/1.5 system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}.price{font-size:2rem;font-weight:700}
  label{display:block;margin:1rem 0}button{background:#a3e635;border:0;padding:.6rem 1rem;border-radius:.6rem;font-weight:600}.error{color:#b91c1c}</style>
</head>
<body>
  <h1>{{ $product['name'] }}</h1>
  <p class="price">£{{ $product['price'] }}</p>

  @error('payment')<p class="error">{{ $message }}</p>@enderror

  <form method="post" action="/checkout">
    @csrf {{-- the shop's own form keeps CSRF protection; only the webhook is exempt --}}
    <label>Pay with
      <select name="coin">
        @foreach ($coins as $key => $coin)
          <option value="{{ $key }}">{{ $coin['label'] }}</option>
        @endforeach
      </select>
    </label>
    <label><input type="checkbox" name="fee_ontop" value="1"> Customer pays the fee on top</label>
    <button>Pay with crypto</button>
  </form>
</body>
</html>
