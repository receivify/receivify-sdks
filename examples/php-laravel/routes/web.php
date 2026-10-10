<?php

use App\Http\Controllers\CheckoutController;
use App\Http\Controllers\ReceivifyWebhookController;
use Illuminate\Support\Facades\Route;

Route::get('/', [CheckoutController::class, 'shop']);
Route::post('/checkout', [CheckoutController::class, 'checkout']);
Route::get('/orders/{order}', [CheckoutController::class, 'show']);

// Excluded from CSRF in bootstrap/app.php: Receivify's servers call this, not a browser form.
Route::post('/webhooks/receivify', ReceivifyWebhookController::class);
