<?php

namespace App\Models;

use Illuminate\Database\Eloquent\Model;

/**
 * An order. Its id is also the Receivify invoice `reference`, which is how webhooks find it.
 */
class Order extends Model
{
    public $incrementing = false;
    protected $keyType = 'string';

    protected $fillable = ['id', 'product', 'amount', 'status', 'invoice_id', 'last_seq'];

    protected $casts = ['last_seq' => 'integer'];
}
