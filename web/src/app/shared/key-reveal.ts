import { Component, input } from '@angular/core';
import { CopyButton } from './copy-button';

/** Shows a new API key once, with the warning that it cannot be shown again. */
@Component({
  selector: 'app-key-reveal',
  imports: [CopyButton],
  templateUrl: './key-reveal.html',
  styleUrl: './key-reveal.scss',
})
export class KeyReveal {
  readonly apiKey = input.required<string>();
}
